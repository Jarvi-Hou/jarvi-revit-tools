using System;
using System.CodeDom.Compiler;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text;
using System.Security.Cryptography;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;
using Microsoft.CSharp;
using Newtonsoft.Json.Linq;

namespace JarviTools.Mcp.Tools
{
    /// <summary>
    /// 在 Revit 中执行任意 C# 代码——类似 Rhino MCP 的 execute_rhinocommon_csharp_code。
    /// 代码运行在 Revit 主线程（通过 ExternalEvent），
    /// 可访问 doc、uidoc、uiapp 等变量，用 output.AppendLine() 返回结果。
    /// 代码的修改会被自动事务包裹，失败时回滚。
    /// 注意：每次调用编译一次，不适合高频重复调用。
    /// </summary>
    public class ExecuteCSharpTool : IRevitTool
    {
        public string Name => "execute_csharp";

        public string Description =>
            "在 Revit 中以完全信任执行任意 C# 代码。只在设置 OPENREVIT_ENABLE_FULL_TRUST_CSHARP=1 时注册，应仅用于受监督的 AI/开发会话。代码可访问 Revit、文件、进程和网络；自动 Transaction 只能回滚 Revit 文档改动，不能回滚外部副作用。";

        public JObject InputSchema => new JObject
        {
            ["type"] = "object",
            ["properties"] = new JObject
            {
                ["code"] = new JObject
                {
                    ["type"] = "string",
                    ["description"] = "要执行的 C# 代码。预置命名空间：System、System.Linq、System.Collections.Generic、Autodesk.Revit.DB、Autodesk.Revit.UI。可用变量：doc(Document)、uidoc(UIDocument)、uiapp(UIApplication)、output(StringBuilder)。用 output.AppendLine() 输出结果。"
                },
                ["useTransaction"] = new JObject
                {
                    ["type"] = "boolean",
                    ["description"] = "是否由工具自动包裹 Revit Transaction（默认 true）。false 仅表示不建立自动事务，仍是完全信任执行，不代表只读。"
                }
            },
            ["required"] = new JArray { "code" },
            ["additionalProperties"] = false
        };

        /// <summary>缓存已编译程序集，避免每次重新编译</summary>
        private const int MaxCachedAssemblies = 32;
        private static readonly Dictionary<string, Assembly> _cache = new Dictionary<string, Assembly>(StringComparer.Ordinal);
        private static readonly Queue<string> _cacheOrder = new Queue<string>();

        public JObject Execute(UIApplication uiapp, JObject input)
        {
            if (uiapp == null) throw new InvalidOperationException("UIApplication is null.");
            var uidoc = uiapp.ActiveUIDocument ?? throw new InvalidOperationException("No active Revit document.");
            var doc   = uidoc.Document       ?? throw new InvalidOperationException("Active UIDocument has no Document.");

            string code = (string)input["code"];
            if (string.IsNullOrWhiteSpace(code))
                throw new ArgumentException("'code' is required and must be non-empty.");

            bool useTransaction = input["useTransaction"] == null || (bool)input["useTransaction"];

            // 编译代码
            var output = new StringBuilder();
            string cacheKey = ComputeCacheKey(code);

            // 包裹用户代码：提供 doc/uidoc/uiapp/output 变量
            string wrapper = @"
using System;
using System.Linq;
using System.Text;
using System.Collections.Generic;
using Autodesk.Revit.DB;
using Autodesk.Revit.UI;

public class RevitScript
{
    public static void Run(Document doc, UIDocument uidoc, UIApplication uiapp, StringBuilder output)
    {
        " + code + @"
    }
}";
            // 编译
            var provider = new CSharpCodeProvider();
            var options = new CompilerParameters
            {
                GenerateInMemory = true,
                GenerateExecutable = false,
                TreatWarningsAsErrors = false,
                IncludeDebugInformation = false
                // 注意：不要设置 /langversion —— .NET Framework 内置的 CodeDom 编译器
                // 只认 ISO-1/ISO-2/3/4/5/Default，传 7.3 会让所有脚本编译失败。
                // 脚本请用 C# 5 语法（无字符串插值、无 ?. 运算符、无 out var）。
            };

            // 引用必需的程序集（严格白名单——只加确切的必要程序集，避免宽泛前缀匹配）
            // 之前用 name.StartsWith("System.") 宽匹配拉入了几百个程序集，
            // 其中 System.Threading.Tasks.Extensions 被不同插件从不同路径加载两次，
            // 导致 CS1703 编译失败。此版本严格限制白名单。
            var assemblyNames = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            {
                "mscorlib",
                "System",
                "System.Core",
                "System.Data",
                "System.Xml",
                "System.Xml.Linq",
                "System.Windows.Forms",
                "System.Runtime",
                "Microsoft.CSharp",
                "Newtonsoft.Json",
                "RevitAPI",
                "RevitAPIUI"
            };
            var assemblies = AppDomain.CurrentDomain.GetAssemblies()
                .Where(a => !a.IsDynamic && !string.IsNullOrEmpty(a.Location))
                .Where(a => assemblyNames.Contains(a.GetName().Name))
                // 按程序集全名去重（包含版本+Token），确保CSC不报CS1703
                .GroupBy(a => a.GetName().FullName, StringComparer.OrdinalIgnoreCase)
                .Select(g => g.OrderBy(a => a.Location.Length).First().Location)
                .ToArray();
            options.ReferencedAssemblies.AddRange(assemblies);

            CompilerResults results;
            if (_cache.TryGetValue(cacheKey, out var cached) && cached != null)
            {
                // 用缓存程序集
                var entryPoint = cached.GetType("RevitScript");
                if (entryPoint == null)
                {
                    // 缓存失效，重新编译
                    _cache.Remove(cacheKey);
                    results = provider.CompileAssemblyFromSource(options, wrapper);
                }
                else
                {
                    try
                    {
                        var method = entryPoint.GetMethod("Run");
                        if (useTransaction)
                        {
                            using (var tx = new Transaction(doc, "Execute C# Script"))
                            {
                                tx.Start();
                                try
                                {
                                    method.Invoke(null, new object[] { doc, uidoc, uiapp, output });
                                }
                                catch (Exception ex)
                                {
                                    if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                                    throw new InvalidOperationException(
                                        "脚本执行失败（已回滚）:\n" +
                                        (ex is TargetInvocationException tie ? tie.InnerException?.Message : ex.Message));
                                }
                                JarviTools.Core.TransactionSafety.Commit(tx, "Execute cached full-trust C# script");
                            }
                        }
                        else
                        {
                            method.Invoke(null, new object[] { doc, uidoc, uiapp, output });
                        }

                        return new JObject
                        {
                            ["success"] = true,
                            ["output"]  = output.ToString(),
                            ["cached"]  = true
                        };
                    }
                    catch
                    {
                        _cache.Remove(cacheKey);
                        throw;
                    }
                }
            }
            else
            {
                results = provider.CompileAssemblyFromSource(options, wrapper);
            }

            if (results.Errors.HasErrors)
            {
                var errors = new StringBuilder();
                foreach (CompilerError err in results.Errors)
                {
                    errors.AppendLine($"行 {err.Line}: {err.ErrorText}");
                }
                return new JObject
                {
                    ["success"] = false,
                    ["error"]   = "编译失败:\n" + errors
                };
            }

            var assembly = results.CompiledAssembly;
            AddToCache(cacheKey, assembly);

            var scriptType = assembly.GetType("RevitScript");
            if (scriptType == null)
                throw new InvalidOperationException("编译后未找到 RevitScript 类。");

            var runMethod = scriptType.GetMethod("Run");
            if (runMethod == null)
                throw new InvalidOperationException("RevitScript 类未包含 Run 方法。");

            try
            {
                if (useTransaction)
                {
                    using (var tx = new Transaction(doc, "Execute C# Script"))
                    {
                        tx.Start();
                        try
                        {
                            runMethod.Invoke(null, new object[] { doc, uidoc, uiapp, output });
                        }
                        catch
                        {
                            if (tx.HasStarted() && !tx.HasEnded()) tx.RollBack();
                            throw;
                        }
                        JarviTools.Core.TransactionSafety.Commit(tx, "Execute full-trust C# script");
                    }
                }
                else
                {
                    runMethod.Invoke(null, new object[] { doc, uidoc, uiapp, output });
                }

                return new JObject
                {
                    ["success"] = true,
                    ["output"]  = output.ToString(),
                    ["cached"]  = false
                };
            }
            catch (TargetInvocationException tie)
            {
                // 解包用户代码的真实异常
                var inner = tie.InnerException;
                return new JObject
                {
                    ["success"] = false,
                    ["error"]   = "运行时错误" + (useTransaction ? "（已回滚）" : "") + ":\n" + (inner?.Message ?? tie.Message)
                };
            }
            catch (Exception ex)
            {
                return new JObject
                {
                    ["success"] = false,
                    ["error"]   = "运行时错误" + (useTransaction ? "（已回滚）" : "") + ":\n" + ex.Message
                };
            }
        }

        private static string ComputeCacheKey(string code)
        {
            using (var hash = SHA256.Create())
                return Convert.ToBase64String(hash.ComputeHash(Encoding.UTF8.GetBytes(code)));
        }

        private static void AddToCache(string key, Assembly assembly)
        {
            if (_cache.ContainsKey(key)) return;
            _cache[key] = assembly;
            _cacheOrder.Enqueue(key);
            while (_cacheOrder.Count > MaxCachedAssemblies)
                _cache.Remove(_cacheOrder.Dequeue());
        }
    }
}

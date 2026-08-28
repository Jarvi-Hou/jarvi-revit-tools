using System;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Text;
using System.Threading.Tasks;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace OpenRevit.McpBridge
{
    internal sealed class RevitHttpClient : IDisposable
    {
        private readonly HttpClient _http;
        private readonly string _configuredToken;
        private readonly int _port;

        public RevitHttpClient(string baseUrl, string token, int port)
        {
            if (string.IsNullOrWhiteSpace(baseUrl))
            {
                throw new ArgumentNullException(nameof(baseUrl));
            }

            _http = new HttpClient
            {
                BaseAddress = new Uri(baseUrl, UriKind.Absolute),
                Timeout = TimeSpan.FromSeconds(310)
            };
            _http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));

            _configuredToken = token;
            _port = port;
        }

        public JObject GetTools()
        {
            try
            {
                using (HttpResponseMessage response = SendSync(HttpMethod.Get, "tools", null))
                {
                    return ParseResponse(response, "GET /tools");
                }
            }
            catch (RevitUnreachableException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new RevitUnreachableException("HTTP GET /tools failed: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new RevitUnreachableException("HTTP GET /tools timed out", ex);
            }
        }

        public JObject CallTool(string toolName, JObject arguments)
        {
            if (string.IsNullOrWhiteSpace(toolName))
            {
                throw new ArgumentNullException(nameof(toolName));
            }

            string json = (arguments ?? new JObject()).ToString(Formatting.None);
            try
            {
                using (var content = new StringContent(json, Encoding.UTF8, "application/json"))
                using (HttpResponseMessage response = SendSync(HttpMethod.Post, "tools/" + toolName, content))
                {
                    return ParseResponse(response, "POST /tools/" + toolName);
                }
            }
            catch (RevitUnreachableException)
            {
                throw;
            }
            catch (HttpRequestException ex)
            {
                throw new RevitUnreachableException("HTTP POST /tools/" + toolName + " failed: " + ex.Message, ex);
            }
            catch (TaskCanceledException ex)
            {
                throw new RevitUnreachableException("HTTP POST /tools/" + toolName + " timed out", ex);
            }
        }

        public JObject GetOperationStatus(string operationId)
        {
            if (string.IsNullOrWhiteSpace(operationId))
                throw new ArgumentNullException(nameof(operationId));
            using (HttpResponseMessage response = SendSync(
                HttpMethod.Get,
                "operations/" + Uri.EscapeDataString(operationId),
                null))
            {
                return ParseResponse(response, "GET /operations/" + operationId);
            }
        }

        private HttpResponseMessage SendSync(HttpMethod method, string relativeUrl, HttpContent content)
        {
            using (var request = new HttpRequestMessage(method, relativeUrl))
            {
                request.Content = content;
                string token = string.IsNullOrWhiteSpace(_configuredToken) ? SessionTokenStore.Read(_port) : _configuredToken;
                if (!string.IsNullOrWhiteSpace(token))
                {
                    request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
                }
                return _http.SendAsync(request).GetAwaiter().GetResult();
            }
        }

        private static JObject ParseResponse(HttpResponseMessage response, string operation)
        {
            string body = response.Content == null
                ? string.Empty
                : response.Content.ReadAsStringAsync().GetAwaiter().GetResult();

            if (!response.IsSuccessStatusCode)
            {
                throw new RevitUnreachableException(
                    operation + " returned HTTP " + (int)response.StatusCode + ": " + body,
                    null);
            }

            try
            {
                return JObject.Parse(body);
            }
            catch (JsonException ex)
            {
                throw new RevitUnreachableException(operation + " returned invalid JSON", ex);
            }
        }

        public void Dispose()
        {
            _http.Dispose();
        }
    }
}

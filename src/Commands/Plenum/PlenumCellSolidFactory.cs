using System;
using System.Collections.Generic;
using Autodesk.Revit.DB;

namespace JarviTools.Commands.Plenum
{
    internal static class PlenumCellSolidFactory
    {
        private const double MmPerFoot = 304.8;

        public static Solid Create(
            XYZ p00,
            XYZ p10,
            XYZ p11,
            XYZ p01,
            double startHeightFt,
            double requestedHeightFt)
        {
            double heightFt = Math.Min(requestedHeightFt, 10000.0 / MmPerFoot);
            if (heightFt < 1.0 / MmPerFoot) heightFt = 1.0 / MmPerFoot;
            try
            {
                XYZ offset = XYZ.BasisZ.Multiply(Math.Max(0.0, startHeightFt));
                var loop = new CurveLoop();
                loop.Append(Line.CreateBound(p00 + offset, p10 + offset));
                loop.Append(Line.CreateBound(p10 + offset, p11 + offset));
                loop.Append(Line.CreateBound(p11 + offset, p01 + offset));
                loop.Append(Line.CreateBound(p01 + offset, p00 + offset));
                return GeometryCreationUtilities.CreateExtrusionGeometry(
                    new List<CurveLoop> { loop }, XYZ.BasisZ, heightFt);
            }
            catch
            {
                return null;
            }
        }
    }
}

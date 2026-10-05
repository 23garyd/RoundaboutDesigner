using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using ArcGIS.Core.CIM;
using ArcGIS.Core.Data;
using ArcGIS.Core.Geometry;
using ArcGIS.Desktop.Mapping;
using ArcGIS.Desktop.Core;
using ArcGIS.Desktop.Core.Geoprocessing;
using ArcGIS.Desktop.Framework.Threading.Tasks;
using RoundaboutDesigner.Models;

namespace RoundaboutDesigner.Services
{
    public class CandidateLayerWriter
    {
        // Writes all candidates to a point feature class in the default GDB.
        // Returns the full path to the created feature class.
        public async Task<string> WriteAsync(
            List<IntersectionCandidate> candidates,
            StudyArea studyArea,
            double thresholdUsed)
        {
            string gdbPath = Project.Current?.DefaultGeodatabasePath;
            if (string.IsNullOrEmpty(gdbPath))
                throw new InvalidOperationException("No default geodatabase found in the current project.");

            // Sanitize a feature-class name from the city (no spaces/punctuation).
            string safeCity = new string((studyArea.CityName ?? "Study")
                .Where(ch => char.IsLetterOrDigit(ch)).ToArray());
            if (string.IsNullOrEmpty(safeCity)) safeCity = "Study";
            string fcName = $"RoundaboutCandidates_{safeCity}";
            string fullPath = Path.Combine(gdbPath, fcName);

            // 1. Create the feature class (POINT, WGS84). Overwrite if it exists.
            var createArgs = Geoprocessing.MakeValueArray(
                gdbPath, fcName, "POINT", "", "DISABLED", "DISABLED",
                SpatialReferences.WGS84);
            var env = Geoprocessing.MakeEnvironmentArray(overwriteoutput: true);

            var createResult = await Geoprocessing.ExecuteToolAsync(
                "management.CreateFeatureclass", createArgs, env,
                null, null, GPExecuteToolFlags.None);
            if (createResult.IsFailed)
                throw new Exception("CreateFeatureclass failed: " +
                    string.Join("; ", createResult.Messages.Select(m => m.Text)));

            // 2. Add fields. AddFields takes a nested value array:
            //    { {name, type, alias, length}, ... }
            var fieldDefs = new List<string[]>
            {
                new[] { "OsmNodeId",   "TEXT",   "OSM Node Id", "30" },
                new[] { "Name",        "TEXT",   "Intersection", "255" },
                new[] { "Control",     "TEXT",   "Control Type", "50" },
                new[] { "Legs",        "SHORT",  "Leg Count", "" },
                new[] { "TotAADT",     "LONG",   "Total Entering AADT", "" },
                new[] { "Fatal5Yr",    "SHORT",  "Fatal Crashes 5Yr", "" },
                new[] { "SafetyS",     "DOUBLE", "Safety Score", "" },
                new[] { "TrafficS",    "DOUBLE", "Traffic Score", "" },
                new[] { "FeasS",       "DOUBLE", "Feasibility Score", "" },
                new[] { "MultiS",      "DOUBLE", "Multimodal Score", "" },
                new[] { "DestS",       "DOUBLE", "Destinations Score", "" },
                new[] { "FinalScore",  "DOUBLE", "Final Score", "" },
                new[] { "RankNum",     "LONG",   "Rank", "" },
                new[] { "Confidence",  "TEXT",   "Confidence", "20" },
                new[] { "RbType",      "TEXT",   "Roundabout Type", "50" },
                new[] { "CircLanes",   "SHORT",  "Circulating Lanes", "" },
                new[] { "DiamFt",      "DOUBLE", "Inscribed Diameter Ft", "" },
                new[] { "Justify",     "TEXT",   "Type Justification", "500" },
                new[] { "CostTotal",   "DOUBLE", "Total Cost", "" },
                new[] { "CostLand",    "DOUBLE", "Land Cost", "" },
                new[] { "CostConst",   "DOUBLE", "Construction Cost", "" },
                new[] { "CostUtil",    "DOUBLE", "Utility Cost", "" },
                new[] { "CostCont",    "DOUBLE", "Contingency", "" },
                new[] { "PassedThresh","SHORT",  "Passed Threshold", "" },
            };

            // Build the nested-array string AddFields expects.
            string fieldDescriptions = string.Join(";", fieldDefs.Select(f =>
                $"{f[0]} {f[1]} '{f[2]}' {(string.IsNullOrEmpty(f[3]) ? "#" : f[3])} # #"));

            var addArgs = Geoprocessing.MakeValueArray(fullPath, fieldDescriptions);
            var addResult = await Geoprocessing.ExecuteToolAsync(
                "management.AddFields", addArgs, null, null, null, GPExecuteToolFlags.None);
            if (addResult.IsFailed)
                throw new Exception("AddFields failed: " +
                    string.Join("; ", addResult.Messages.Select(m => m.Text)));

            // 3. Bulk-insert rows.
            await QueuedTask.Run(() =>
            {
                using var gdb = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                using var fc = gdb.OpenDataset<FeatureClass>(fcName);
                // using var rowBuffer = fc.CreateRowBuffer();

                using var cursor = fc.CreateInsertCursor();
                foreach (var c in candidates)
                {
                    using var rb = fc.CreateRowBuffer();
                    rb[fc.GetDefinition().GetShapeField()] = c.Location;

                    rb["OsmNodeId"] = c.OsmNodeId.ToString();
                    rb["Name"] = Trunc(c.IntersectionName, 255);
                    rb["Control"] = Trunc(c.ControlType, 50);
                    rb["Legs"] = (short)c.LegCount;
                    rb["TotAADT"] = c.TotalEnteringAadt;
                    rb["Fatal5Yr"] = (short)c.FatalCrashes5Yr;
                    rb["SafetyS"] = c.SafetyScore;
                    rb["TrafficS"] = c.TrafficScore;
                    rb["FeasS"] = c.FeasibilityScore;
                    rb["MultiS"] = c.MultimodalScore;
                    rb["DestS"] = c.DestinationsScore;
                    rb["FinalScore"] = c.FinalScore;
                    rb["RankNum"] = c.Rank;
                    rb["Confidence"] = Trunc(c.ConfidenceLevel, 20);
                    rb["RbType"] = Trunc(c.Recommendation?.TypeLabel, 50);
                    rb["CircLanes"] = (short)(c.Recommendation?.CirculatingLanes ?? 0);
                    rb["DiamFt"] = c.Recommendation?.InscribedDiameterFt ?? 0;
                    rb["Justify"] = Trunc(c.Recommendation?.Justification, 500);
                    rb["CostTotal"] = c.Cost?.Total ?? 0;
                    rb["CostLand"] = c.Cost?.LandAcquisition ?? 0;
                    rb["CostConst"] = c.Cost?.Construction ?? 0;
                    rb["CostUtil"] = c.Cost?.UtilityRelocation ?? 0;
                    rb["CostCont"] = c.Cost?.Contingency ?? 0;
                    rb["PassedThresh"] = (short)(c.FinalScore >= thresholdUsed ? 1 : 0);

                    cursor.Insert(rb);
                }
                cursor.Flush();
            });

            // Add the new feature class to the active map.
            // Add the new feature class to the active map, symbolized by FinalScore.
            await QueuedTask.Run(() =>
            {
                var map = MapView.Active?.Map;
                if (map == null) return; // no map open; the FC still exists in the GDB

                // Remove any prior layer of the same name (avoids duplicates on re-run).
                var existing = map.GetLayersAsFlattenedList()
                    .OfType<FeatureLayer>()
                    .Where(l => l.Name == fcName)
                    .ToList();
                foreach (var lyr in existing)
                    map.RemoveLayer(lyr);

                using var gdb = new Geodatabase(
                    new FileGeodatabaseConnectionPath(new Uri(gdbPath)));
                using var fc = gdb.OpenDataset<FeatureClass>(fcName);

                // Red (low) -> yellow (mid) -> green (high) continuous ramp.
                var colorRamp = BuildScoreColorRamp();

                // Point symbol template; graduated colors override the fill per break.
                var symbolTemplate = SymbolFactory.Instance.ConstructPointSymbol(
                    ColorFactory.Instance.GreyRGB, 10.0, SimpleMarkerStyle.Circle)
                    .MakeSymbolReference();

                var gcDef = new GraduatedColorsRendererDefinition()
                {
                    ClassificationField = "FinalScore",
                    ClassificationMethod = ClassificationMethod.NaturalBreaks,
                    BreakCount = 5,
                    ColorRamp = colorRamp,
                    SymbolTemplate = symbolTemplate
                };

                var fcParams = new FeatureLayerCreationParams(fc)
                {
                    Name = fcName,
                    IsVisible = true,
                    RendererDefinition = gcDef
                };

                LayerFactory.Instance.CreateLayer<FeatureLayer>(fcParams, map);
            });

            return fullPath;
        }

        // Builds a red -> yellow -> green continuous color ramp for FinalScore,
        // so low-scoring candidates render red and high-scoring render green.
        private static CIMColorRamp BuildScoreColorRamp()
        {
            var red = new CIMRGBColor { R = 215, G = 48, B = 39, Alpha = 100 };    // low
            var yellow = new CIMRGBColor { R = 255, G = 255, B = 191, Alpha = 100 }; // mid
            var green = new CIMRGBColor { R = 26, G = 152, B = 80, Alpha = 100 };   // high

            // Two segments: red->yellow, yellow->green, joined into a multipart ramp.
            var lowHalf = new CIMLinearContinuousColorRamp { FromColor = red, ToColor = yellow };
            var highHalf = new CIMLinearContinuousColorRamp { FromColor = yellow, ToColor = green };

            return new CIMMultipartColorRamp
            {
                ColorRamps = new CIMColorRamp[] { lowHalf, highHalf },
                Weights = new double[] { 1.0, 1.0 }
            };
        }

        private static string Trunc(string s, int max) =>
            string.IsNullOrEmpty(s) ? s : (s.Length > max ? s.Substring(0, max) : s);
    }
}
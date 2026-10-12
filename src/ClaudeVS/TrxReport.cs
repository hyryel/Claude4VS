using System.Linq;
using System.Xml.Linq;
using Newtonsoft.Json.Linq;

namespace ClaudeVS
{
    /// <summary>Agrégation des rapports TRX produits par « dotnet test ». Sans dépendance à Visual Studio.</summary>
    internal sealed class TrxSummary
    {
        public long Total { get; set; }
        public long Passed { get; set; }
        public long Failed { get; set; }
        /// <summary>Ignorés, non concluants… : tout ce qui n'est ni réussi ni échoué.</summary>
        public long Skipped => System.Math.Max(0, Total - Passed - Failed);
        public JArray Failures { get; set; } = new JArray();
    }

    internal static class TrxReport
    {
        private static readonly XNamespace Ns = "http://microsoft.com/schemas/VisualStudio/TeamTest/2010";

        /// <summary>Un TRX par projet de tests : les compteurs sont sommés et les échecs concaténés.</summary>
        public static TrxSummary Read(string[] trx)
        {
            var summary = new TrxSummary();
            var failures = summary.Failures;
            foreach (var file in trx)
            {
                var doc = XDocument.Load(file);
                var counters = doc.Descendants(Ns + "Counters").FirstOrDefault();
                if (counters != null)
                {
                    summary.Total += Attr(counters, "total");
                    summary.Passed += Attr(counters, "passed");
                    summary.Failed += Attr(counters, "failed");
                }
                foreach (var r in doc.Descendants(Ns + "UnitTestResult"))
                {
                    var outcome = (string)r.Attribute("outcome");
                    if (outcome == "Passed" || outcome == "NotExecuted") continue;
                    var error = r.Descendants(Ns + "ErrorInfo").FirstOrDefault();
                    var stack = (string)error?.Element(Ns + "StackTrace");
                    failures.Add(new JObject
                    {
                        ["test"] = (string)r.Attribute("testName"),
                        ["outcome"] = outcome,
                        ["duration"] = (string)r.Attribute("duration"),
                        ["message"] = Truncate((string)error?.Element(Ns + "Message"), 2000),
                        ["stackTrace"] = Truncate(stack, 1500),
                    });
                }
            }
            return summary;
        }

        private static long Attr(XElement e, string name) => long.TryParse((string)e.Attribute(name), out var v) ? v : 0;
        private static string Truncate(string s, int n) => s != null && s.Length > n ? s.Substring(0, n) + "…" : s;
    }
}

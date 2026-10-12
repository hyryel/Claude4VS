using System;
using System.IO;
using Xunit;

namespace ClaudeVS.Tests
{
    public class TrxReportTests : IDisposable
    {
        private readonly string _dir = Path.Combine(Path.GetTempPath(), "ClaudeVS.Tests", Guid.NewGuid().ToString("N"));
        public TrxReportTests() => Directory.CreateDirectory(_dir);
        public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

        private string Trx(string name, string counters, string results)
        {
            var path = Path.Combine(_dir, name);
            File.WriteAllText(path, "<?xml version=\"1.0\" encoding=\"UTF-8\"?><TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\">"
                + "<Results>" + results + "</Results><ResultSummary outcome=\"Failed\"><Counters " + counters + " /></ResultSummary></TestRun>");
            return path;
        }

        private static string Failed(string name, string msg, string stack = "at X")
            => $"<UnitTestResult testName=\"{name}\" outcome=\"Failed\" duration=\"00:00:01\"><Output><ErrorInfo><Message>{msg}</Message><StackTrace>{stack}</StackTrace></ErrorInfo></Output></UnitTestResult>";

        [Fact]
        public void Les_compteurs_de_plusieurs_fichiers_sont_sommes()
        {
            var a = Trx("a.trx", "total=\"10\" passed=\"7\" failed=\"2\"", "");
            var b = Trx("b.trx", "total=\"5\" passed=\"5\" failed=\"0\"", "");
            var s = TrxReport.Read(new[] { a, b });
            Assert.Equal(15, s.Total);
            Assert.Equal(12, s.Passed);
            Assert.Equal(2, s.Failed);
        }

        [Fact]
        public void Skipped_est_total_moins_reussis_moins_echoues()
        {
            var s = TrxReport.Read(new[] { Trx("a.trx", "total=\"10\" passed=\"6\" failed=\"1\"", ""), Trx("b.trx", "total=\"4\" passed=\"2\" failed=\"0\"", "") });
            Assert.Equal(5, s.Skipped);
        }

        [Fact]
        public void Les_echecs_de_tous_les_fichiers_sont_concatenes_sans_les_reussis_ni_NotExecuted()
        {
            var a = Trx("a.trx", "total=\"3\" passed=\"1\" failed=\"1\"",
                Failed("T1", "boum") + "<UnitTestResult testName=\"Ok\" outcome=\"Passed\" duration=\"0\" />"
                + "<UnitTestResult testName=\"Ign\" outcome=\"NotExecuted\" duration=\"0\" />");
            var b = Trx("b.trx", "total=\"1\" passed=\"0\" failed=\"1\"", Failed("T2", "bang", "at Y"));
            var s = TrxReport.Read(new[] { a, b });
            Assert.Equal(2, s.Failures.Count);
            Assert.Equal("T1", (string)s.Failures[0]["test"]);
            Assert.Equal("Failed", (string)s.Failures[0]["outcome"]);
            Assert.Equal("boum", (string)s.Failures[0]["message"]);
            Assert.Equal("T2", (string)s.Failures[1]["test"]);
            Assert.Equal("at Y", (string)s.Failures[1]["stackTrace"]);
        }

        [Fact]
        public void Message_tronque_a_2000_et_trace_a_1500_caracteres()
        {
            var a = Trx("a.trx", "total=\"1\" passed=\"0\" failed=\"1\"", Failed("T", new string('m', 3000), new string('s', 3000)));
            var f = TrxReport.Read(new[] { a }).Failures[0];
            Assert.Equal(new string('m', 2000) + "…", (string)f["message"]);
            Assert.Equal(new string('s', 1500) + "…", (string)f["stackTrace"]);
        }

        [Fact]
        public void Fichier_sans_compteurs_donne_zero_et_aucun_echec_donne_liste_vide()
        {
            var path = Path.Combine(_dir, "e.trx");
            File.WriteAllText(path, "<TestRun xmlns=\"http://microsoft.com/schemas/VisualStudio/TeamTest/2010\" />");
            var s = TrxReport.Read(new[] { path });
            Assert.Equal(0, s.Total);
            Assert.Equal(0, s.Skipped);
            Assert.Empty(s.Failures);
        }
    }
}

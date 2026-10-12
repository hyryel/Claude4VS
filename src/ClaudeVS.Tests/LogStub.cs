namespace ClaudeVS
{
    // Substitut minimal du vrai Log (défini dans ClaudeVSPackage.cs, qui dépend de Visual Studio)
    internal sealed class Log
    {
        public void Info(string m) { }
        public void Error(string m) { }
    }
}

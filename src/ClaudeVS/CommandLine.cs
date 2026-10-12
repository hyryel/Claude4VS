using System.Collections.Generic;
using System.Linq;
using System.Text;

namespace ClaudeVS
{
    /// <summary>
    /// Échappement d'arguments de ligne de commande selon les règles de CommandLineToArgvW (CRT Windows) :
    /// les backslashes qui précèdent un guillemet sont doublés (+1 pour échapper le guillemet), ceux en fin
    /// d'argument sont doublés, un argument vide devient "" ; pas de guillemets sans espace, tabulation ni guillemet.
    /// </summary>
    internal static class CommandLine
    {
        public static string Quote(string arg)
        {
            arg = arg ?? "";
            if (arg.Length > 0 && arg.IndexOfAny(new[] { ' ', '\t', '"' }) < 0) return arg;
            var sb = new StringBuilder("\"");
            var backslashes = 0;
            foreach (var c in arg)
            {
                if (c == '\\') { backslashes++; continue; }
                if (c == '"') { sb.Append('\\', backslashes * 2 + 1); sb.Append('"'); }
                else { sb.Append('\\', backslashes); sb.Append(c); }
                backslashes = 0;
            }
            sb.Append('\\', backslashes * 2);
            sb.Append('"');
            return sb.ToString();
        }

        public static string Join(IEnumerable<string> args) => string.Join(" ", args.Select(Quote));
    }
}

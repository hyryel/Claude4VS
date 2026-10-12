using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using Newtonsoft.Json.Linq;
#nullable enable

namespace ClaudeVS
{
    /// <summary>Un groupe d'outils MCP : ses définitions, son routage et sa visibilité pour le modèle.</summary>
    internal interface IToolGroup
    {
        /// <summary>Définitions MCP du groupe.</summary>
        JArray Definitions();

        bool Handles(string name);

        /// <summary>Exposé au CLI du panneau (chemin /tools).</summary>
        bool ModelVisible(string name);

        Task<JObject> CallAsync(string name, JObject args, CancellationToken ct);
    }

    /// <summary>Base des groupes : les noms gérés sont dérivés des définitions (une seule déclaration par outil).</summary>
    internal abstract class ToolGroup : IToolGroup
    {
        private HashSet<string?>? _names;

        public abstract JArray Definitions();

        public bool Handles(string name) =>
            (_names ?? (_names = new HashSet<string?>(Definitions().Select(t => (string?)t["name"])))).Contains(name);

        public virtual bool ModelVisible(string name) => true;

        public abstract Task<JObject> CallAsync(string name, JObject args, CancellationToken ct);
    }
}

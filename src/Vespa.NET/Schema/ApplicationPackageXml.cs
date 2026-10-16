using System.Globalization;
using System.Security;
using System.Text;

namespace Vespa.Models.Schema;

/// <summary>Renders services.xml and hosts.xml for generated application packages.</summary>
internal static class ApplicationPackageXml
{
    private static string XmlEscape(string value) => SecurityElement.Escape(value)!;

    internal static string GenerateServicesXml(
        IReadOnlyList<(string DocType, bool Global, DocumentMode Mode, string? Selection)> docInfos,
        ApplicationPackageOptions opts)
    {
        var multiNode = opts.NodeCount > 1;
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<services version=\"1.0\">\n");
        sb.Append("  <container id=\"default\" version=\"1.0\">\n");
        sb.Append("    <search/>\n");
        sb.Append("    <document-api/>\n");

        if (opts.ContainerOptions is { Count: > 0 })
        {
            foreach (var fragment in opts.ContainerOptions.Values)
                sb.Append("    ").Append(fragment.Trim()).Append('\n');
        }

        if (multiNode)
        {
            sb.Append("    <nodes>\n");
            for (var i = 0; i < opts.NodeCount; i++)
                sb.Append(CultureInfo.InvariantCulture, $"      <node hostalias=\"node{i + 1}\"/>\n");
            sb.Append("    </nodes>\n");
        }

        sb.Append("  </container>\n");
        sb.Append("  <content id=\"content\" version=\"1.0\">\n");
        sb.Append(CultureInfo.InvariantCulture, $"    <redundancy>{opts.Redundancy}</redundancy>\n");

        // Any per-document selection implies garbage collection: documents no longer
        // matching their selection are removed by the content nodes.
        var garbageCollection = docInfos.Any(d => d.Selection is not null)
            ? " garbage-collection=\"true\""
            : "";
        sb.Append(CultureInfo.InvariantCulture, $"    <documents{garbageCollection}>\n");

        foreach (var (docType, global, docMode, selection) in docInfos)
        {
            var mode = docMode switch
            {
                DocumentMode.Streaming => "streaming",
                DocumentMode.StoreOnly => "store-only",
                _ => "index"
            };
            var globalAttr = global ? " global=\"true\"" : "";
            var selectionAttr = selection is not null ? $" selection=\"{XmlEscape(selection)}\"" : "";
            sb.Append(CultureInfo.InvariantCulture, $"      <document type=\"{XmlEscape(docType)}\" mode=\"{mode}\"{globalAttr}{selectionAttr}/>\n");
        }

        sb.Append("    </documents>\n");
        sb.Append("    <nodes>\n");
        for (var i = 0; i < opts.NodeCount; i++)
            sb.Append(CultureInfo.InvariantCulture, $"      <node hostalias=\"node{i + 1}\" distribution-key=\"{i}\"/>\n");
        sb.Append("    </nodes>\n");

        if (opts.ContentOptions is { Count: > 0 })
        {
            foreach (var fragment in opts.ContentOptions.Values)
                sb.Append("    ").Append(fragment.Trim()).Append('\n');
        }

        sb.Append("  </content>\n");
        sb.Append("</services>\n");
        return sb.ToString();
    }

    internal static string GenerateHostsXml(ApplicationPackageOptions opts)
    {
        var sb = new StringBuilder();
        sb.Append("<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n");
        sb.Append("<hosts>\n");
        for (var i = 0; i < opts.NodeCount; i++)
        {
            var alias = $"node{i + 1}";
            var hostName = opts.HostNames is not null ? opts.HostNames[i] : alias;
            sb.Append(CultureInfo.InvariantCulture, $"  <host name=\"{XmlEscape(hostName)}\">\n");
            sb.Append(CultureInfo.InvariantCulture, $"    <alias>{XmlEscape(alias)}</alias>\n");
            sb.Append("  </host>\n");
        }

        sb.Append("</hosts>\n");
        return sb.ToString();
    }
}

namespace Vespa.Models.Schema;

/// <summary>Renders the intermediate schema model as a Vespa schema.</summary>
internal static class SchemaEmitter
{
    internal static string Emit(SchemaModel model)
    {
        var writer = new SchemaWriter();
        EmitSchema(writer, model);
        return writer.ToString();
    }

    private static void EmitSchema(SchemaWriter w, SchemaModel model)
    {
        // A schema inheriting another schema must also declare the document
        // inheritance explicitly, otherwise Vespa rejects the package at deploy.
        var inherits = model.Inherits is not null ? $" inherits {model.Inherits}" : "";
        w.Open($"schema {model.DocType}{inherits}");
        w.Open($"document {model.DocType}{inherits}");

        foreach (var s in model.Structs)
        {
            w.Open($"struct {s.Name}");
            foreach (var (name, vespaType) in s.Fields)
                w.Line($"field {name} type {vespaType} {{}}");
            w.Close();
        }

        foreach (var field in model.Fields)
            EmitField(w, field);

        w.Close(); // document

        // Import fields (schema-level, outside document block)
        foreach (var imp in model.ImportFields)
            w.Line($"import field {imp.ReferenceField}.{imp.ForeignField} as {imp.Alias} {{}}");

        foreach (var constant in model.Constants)
        {
            w.Open($"constant {constant.Name}");
            w.Line($"file: {constant.File}");
            w.Line($"type: {constant.TensorType}");
            w.Close();
        }

        foreach (var onnx in model.OnnxModels)
        {
            w.Open($"onnx-model {onnx.Name}");
            w.Line($"file: {onnx.File}");
            w.Close();
        }

        foreach (var fieldSet in model.FieldSets)
        {
            w.Open($"fieldset {fieldSet.Name}");
            w.Line($"fields: {string.Join(", ", fieldSet.Fields)}");
            w.Close();
        }

        foreach (var summary in model.DocumentSummaries)
        {
            var summaryInherits = summary.Inherits is not null ? $" inherits {summary.Inherits}" : "";
            w.Open($"document-summary {summary.Name}{summaryInherits}");
            foreach (var field in summary.Fields)
                w.Line($"summary {field} {{}}");
            w.Close();
        }

        foreach (var profile in model.RankProfiles)
            EmitRankProfile(w, profile, model.TensorInputs);

        w.Close(); // schema
    }

    private static void EmitField(SchemaWriter w, SchemaFieldModel field)
    {
        w.Open($"field {field.Name} type {field.VespaType}");

        if (field.Indexing is not null)
            w.Line($"indexing: {field.Indexing}");

        if (field.IsTensor)
        {
            if (field.DistanceMetric is not null)
            {
                w.Open("attribute");
                w.Line($"distance-metric: {field.DistanceMetric}");
                w.Close();
            }

            if (field.HnswIndex)
            {
                w.Open("index");
                w.Open("hnsw");
                w.Line($"max-links-per-node: {field.MaxLinksPerNode}");
                w.Line($"neighbors-to-explore-at-insert: {field.NeighborsToExploreAtInsert}");
                w.Close();
                w.Close();
            }
        }

        if (field.PredicateArity is { } arity)
        {
            w.Open("index");
            w.Line($"arity: {arity}");
            if (field.PredicateLowerBound is { } lower)
                w.Line($"lower-bound: {lower}");
            if (field.PredicateUpperBound is { } upper)
                w.Line($"upper-bound: {upper}");
            w.Close();
        }

        if (field.Match is not null)
            w.Line($"match: {field.Match}");
        if (field.Stemming is not null)
            w.Line($"stemming: {field.Stemming}");
        if (field.Bolding)
            w.Line("bolding: on");
        if (field.FastSearch)
            w.Line("attribute: fast-search");
        if (field.Normalizing is not null)
            w.Line($"normalizing: {field.Normalizing}");

        // Field-level rank accepts only filter/normal; identity/about/tags/empty
        // belong to the separate rank-type element.
        if (field.Rank is not null)
            w.Line($"rank: {field.Rank}");
        if (field.RankTypeName is not null)
            w.Line($"rank-type: {field.RankTypeName}");

        foreach (var (path, indexing) in field.StructFields)
            w.Line($"struct-field {path} {{ indexing: {indexing} }}");

        w.Close();
    }

    private static void EmitRankProfile(
        SchemaWriter w,
        RankProfileModel profile,
        IReadOnlyList<(string QueryName, string TensorType)> tensorInputs)
    {
        var inherits = profile.Inherits is not null ? $" inherits {profile.Inherits}" : "";
        w.Open($"rank-profile {profile.Name}{inherits}");

        if (profile.EmitInputs && tensorInputs.Count > 0)
        {
            w.Open("inputs");
            foreach (var (queryName, tensorType) in tensorInputs)
                w.Line($"{queryName} {tensorType}");
            w.Close();
        }

        if (profile.FirstPhase is not null)
        {
            w.Open("first-phase");
            w.Line($"expression: {profile.FirstPhase}");
            w.Close();
        }

        if (profile.SecondPhase is not null)
        {
            w.Open("second-phase");
            w.Line($"expression: {profile.SecondPhase}");
            if (profile.SecondPhaseRerankCount > 0)
                w.Line($"rerank-count: {profile.SecondPhaseRerankCount}");
            w.Close();
        }

        if (profile.GlobalPhase is not null)
        {
            w.Open("global-phase");
            w.Line($"expression: {profile.GlobalPhase}");
            if (profile.GlobalPhaseRerankCount > 0)
                w.Line($"rerank-count: {profile.GlobalPhaseRerankCount}");
            w.Close();
        }

        if (profile.MatchFeatures is not null)
            w.Line($"match-features: {profile.MatchFeatures}");
        if (profile.SummaryFeatures is not null)
            w.Line($"summary-features: {profile.SummaryFeatures}");

        if (profile.DiversityAttribute is not null)
        {
            w.Open("diversity");
            w.Line($"attribute: {profile.DiversityAttribute}");
            if (profile.DiversityMinGroups > 0)
                w.Line($"min-groups: {profile.DiversityMinGroups}");
            w.Close();
        }

        foreach (var function in profile.Functions)
        {
            // Vespa's schema grammar requires a parameter list even for zero-argument
            // functions ("function name() { … }") — a bare name fails deployment.
            var signature = function.Signature.Contains('(')
                ? function.Signature
                : $"{function.Signature}()";
            w.Open($"function {signature}");
            w.Line($"expression: {function.Expression}");
            w.Close();
        }

        w.Close();
    }
}

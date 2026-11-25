using System.Collections.Frozen;
using System.Linq.Expressions;
using Vespa.Documents;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>Configures generated application packages using model metadata.</summary>
public static class ApplicationPackageOptionsExtensions
{
    /// <summary>Returns new options with a named rank profile for the given model.</summary>
    /// <remarks>
    /// The builder is evaluated once. The profile must have a distinct name on its schema.
    /// </remarks>
    public static ApplicationPackageOptions WithRankProfile<T>(
        this ApplicationPackageOptions options, string name, Action<RankProfileBuilder<T>> configure)
        where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        RankProfileBuilder<T>.ValidateProfileName(name, nameof(name));
        ArgumentNullException.ThrowIfNull(configure);
        var (documentType, _) = VespaDocumentMeta.For<T>();
        var type = typeof(T);
        var existing = options.RankProfiles?.GetValueOrDefault(type) ?? [];
        if (existing.Any(profile => profile.Name == name))
            throw new ArgumentException($"A rank profile named '{name}' is already configured for '{documentType}'.", nameof(name));

        var builder = new RankProfileBuilder<T>();
        configure(builder);
        var profiles = options.RankProfiles is null
            ? new Dictionary<Type, IReadOnlyList<RankProfileDefinition>>()
            : new Dictionary<Type, IReadOnlyList<RankProfileDefinition>>(options.RankProfiles);
        profiles[type] = [.. existing, builder.Build(name)];
        return options with { RankProfiles = profiles.ToFrozenDictionary() };
    }

    /// <summary>Returns new options with a keep selection for the model's document type.</summary>
    /// <remarks>
    /// Captured values are frozen now. The selection controls feed routing and enables garbage collection.
    /// Native expressions, including moving cutoffs using now(), belong in DocumentSelections.
    /// Duplicate targets and CustomServicesXml throw; attribute conflicts are checked during generation.
    /// </remarks>
    public static ApplicationPackageOptions WithDocumentSelection<T>(
        this ApplicationPackageOptions options,
        Expression<Func<T, bool>> selection) where T : class
    {
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(selection);
        if (options.CustomServicesXml is not null)
            throw new ArgumentException("Document selections cannot be combined with CustomServicesXml.", nameof(options));
        var (documentType, _) = VespaDocumentMeta.For<T>();
        if (options.DocumentSelections?.ContainsKey(documentType) == true)
            throw new ArgumentException($"A document selection is already configured for '{documentType}'.", nameof(options));

        var rendered = DocumentCondition.RenderForPackage(documentType, selection);
        var selections = options.DocumentSelections is null
            ? new Dictionary<string, string>(StringComparer.Ordinal)
            : new Dictionary<string, string>(options.DocumentSelections, StringComparer.Ordinal);
        selections.Add(documentType, rendered);
        return options with { DocumentSelections = selections };
    }
}

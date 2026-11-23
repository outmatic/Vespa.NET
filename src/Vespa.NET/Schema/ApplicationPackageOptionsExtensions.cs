using System.Linq.Expressions;
using Vespa.Documents;
using Vespa.Models.Attributes;

namespace Vespa.Models.Schema;

/// <summary>Configures generated application packages using model metadata.</summary>
public static class ApplicationPackageOptionsExtensions
{
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

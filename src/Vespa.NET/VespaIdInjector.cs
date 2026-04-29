using System.Collections.Concurrent;
using System.Net.Http.Json;
using System.Reflection;
using System.Text.Json;
using Vespa.Models;
using Vespa.Models.Attributes;

namespace Vespa;

/// <summary>
/// Post-deserialization helper that copies the wrapper-level document ID
/// into the <see cref="VespaIdAttribute"/>-marked property on the fields object.
/// Vespa's JSON responses carry the ID at the envelope level, not inside
/// the <c>fields</c> object, so the property would otherwise stay empty.
/// </summary>
internal static class VespaIdInjector
{
    private static readonly ConcurrentDictionary<Type, Action<object, string>?> SetterCache = new();

    /// <summary>
    /// Deserializes a <see cref="VespaDocument{T}"/> from HTTP content and
    /// injects the wrapper-level ID into the <see cref="VespaIdAttribute"/>-marked property.
    /// </summary>
    internal static async Task<VespaDocument<T>?> DeserializeAndInjectAsync<T>(
        HttpContent content, CancellationToken cancellationToken) where T : class
    {
        var doc = await content.ReadFromJsonAsync<VespaDocument<T>>(VespaJsonOptions.Default, cancellationToken);
        if (doc is not null)
            Inject(doc);
        return doc;
    }

    /// <summary>
    /// Deserializes a <see cref="VespaDocument{T}"/> from a JSON string and
    /// injects the wrapper-level ID into the <see cref="VespaIdAttribute"/>-marked property.
    /// </summary>
    internal static VespaDocument<T>? DeserializeAndInject<T>(string json) where T : class
    {
        var doc = JsonSerializer.Deserialize<VespaDocument<T>>(json, VespaJsonOptions.Default);
        if (doc is not null)
            Inject(doc);
        return doc;
    }

    /// <summary>
    /// Deserializes a <see cref="VespaSearchResponse{T}"/> from HTTP content and
    /// injects wrapper-level IDs into the <see cref="VespaIdAttribute"/>-marked property on each hit.
    /// </summary>
    internal static async Task<VespaSearchResponse<T>?> DeserializeSearchAndInjectAsync<T>(
        HttpContent content, CancellationToken cancellationToken) where T : class
    {
        var result = await content.ReadFromJsonAsync<VespaSearchResponse<T>>(VespaJsonOptions.Default, cancellationToken);
        if (result is not null)
            foreach (var hit in result.Root.Children)
                Inject(hit);
        return result;
    }

    /// <summary>
    /// Deserializes a <see cref="VespaVisitResponse{T}"/> from HTTP content and
    /// injects wrapper-level IDs into the <see cref="VespaIdAttribute"/>-marked property on each document.
    /// </summary>
    internal static async Task<VespaVisitResponse<T>?> DeserializeVisitAndInjectAsync<T>(
        HttpContent content, CancellationToken cancellationToken) where T : class
    {
        var page = await content.ReadFromJsonAsync<VespaVisitResponse<T>>(VespaJsonOptions.Default, cancellationToken);
        if (page is not null)
            foreach (var doc in page.Documents)
                Inject(doc);
        return page;
    }

    internal static void Inject<T>(VespaDocument<T> doc) where T : class
    {
        if (doc.Fields is null || string.IsNullOrEmpty(doc.Id))
            return;

        var setter = GetOrCreateSetter(typeof(T));
        setter?.Invoke(doc.Fields, doc.Id);
    }

    internal static void Inject<T>(SearchHit<T> hit) where T : class
    {
        if (hit.Fields is null || string.IsNullOrEmpty(hit.Id))
            return;

        var setter = GetOrCreateSetter(typeof(T));
        setter?.Invoke(hit.Fields, hit.Id);
    }

    private static Action<object, string>? GetOrCreateSetter(Type type) =>
        SetterCache.GetOrAdd(type, static t =>
        {
            var prop = Array.Find(
                t.GetProperties(BindingFlags.Public | BindingFlags.Instance),
                p => p.GetCustomAttribute<VespaIdAttribute>() is not null);

            if (prop is null || !prop.CanWrite || prop.PropertyType != typeof(string))
                return null;

            return (obj, id) => prop.SetValue(obj, id);
        });
}

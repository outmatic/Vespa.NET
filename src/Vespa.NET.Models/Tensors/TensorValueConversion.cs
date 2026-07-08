using System.Numerics;

namespace Vespa.Models.Tensors;

/// <summary>
/// Numeric conversions between the concrete storage types of tensor values
/// (float, double, sbyte, Half) and a caller-requested element type.
/// Conversions are checked: out-of-range values throw <see cref="OverflowException"/>,
/// unknown storage types throw <see cref="InvalidCastException"/>.
/// </summary>
internal static class TensorValueConversion
{
    internal static T[]? ToArray<T>(Array? source) where T : struct, INumber<T> => source switch
    {
        null => null,
        T[] typed => typed,
        float[] values => Array.ConvertAll(values, v => T.CreateChecked(v)),
        double[] values => Array.ConvertAll(values, v => T.CreateChecked(v)),
        sbyte[] values => Array.ConvertAll(values, v => T.CreateChecked(v)),
        Half[] values => Array.ConvertAll(values, v => T.CreateChecked((float)v)),
        _ => throw new InvalidCastException(
            $"Tensor values of type {source.GetType()} cannot be converted to {typeof(T)}[].")
    };

    internal static Dictionary<string, T>? ToValueDictionary<T>(object? source) where T : struct, INumber<T> => source switch
    {
        null => null,
        Dictionary<string, T> typed => typed,
        Dictionary<string, float> values => Convert<float, T>(values, static v => v),
        Dictionary<string, double> values => Convert<double, T>(values, static v => v),
        Dictionary<string, sbyte> values => Convert<sbyte, T>(values, static v => v),
        Dictionary<string, Half> values => Convert<Half, T>(values, static v => (float)v),
        _ => throw new InvalidCastException(
            $"Mapped tensor values of type {source.GetType()} cannot be converted to Dictionary<string, {typeof(T)}>.")
    };

    internal static Dictionary<string, T[]>? ToArrayDictionary<T>(object? source) where T : struct, INumber<T> => source switch
    {
        null => null,
        Dictionary<string, T[]> typed => typed,
        Dictionary<string, float[]> values => ConvertArrays<float, T>(values),
        Dictionary<string, double[]> values => ConvertArrays<double, T>(values),
        Dictionary<string, sbyte[]> values => ConvertArrays<sbyte, T>(values),
        Dictionary<string, Half[]> values => ConvertArrays<Half, T>(values),
        _ => throw new InvalidCastException(
            $"Mixed tensor values of type {source.GetType()} cannot be converted to Dictionary<string, {typeof(T)}[]>.")
    };

    private static Dictionary<string, TOut> Convert<TIn, TOut>(Dictionary<string, TIn> source, Func<TIn, double> toDouble)
        where TOut : struct, INumber<TOut>
    {
        var result = new Dictionary<string, TOut>(source.Count);
        foreach (var (key, value) in source)
            result[key] = TOut.CreateChecked(toDouble(value));
        return result;
    }

    private static Dictionary<string, TOut[]> ConvertArrays<TIn, TOut>(Dictionary<string, TIn[]> source)
        where TIn : struct, INumber<TIn>
        where TOut : struct, INumber<TOut>
    {
        var result = new Dictionary<string, TOut[]>(source.Count);
        foreach (var (key, value) in source)
            result[key] = ToArray<TOut>(value)!;
        return result;
    }
}

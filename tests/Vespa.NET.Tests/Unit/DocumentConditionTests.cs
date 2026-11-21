using System.Globalization;
using System.Linq.Expressions;
using Moq;
using Vespa.Documents;
using Vespa.Models.Attributes;
using Xunit;

namespace Vespa.Tests.Unit;

public sealed class DocumentConditionTests
{
    [Fact]
    public void Predicate_MapsDocumentAndFieldsAndPreservesGrouping()
    {
        const decimal minimum = 12.5m;
        var expectedName = "A*?";
        Assert.Equal("(((inventory.unit_price >= 12.5) and (inventory.display_name == \"A*?\")) or not ((inventory.is_active == true)))",
            DocumentCondition.For<Inventory>(p => p.Price >= minimum && p.Name == expectedName || !p.IsActive));
    }

    [Fact]
    public void NumericValues_UseInvariantCulture()
    {
        var previous = CultureInfo.CurrentCulture;
        try
        {
            CultureInfo.CurrentCulture = CultureInfo.GetCultureInfo("it-IT");
            var threshold = 12.25;
            Assert.Equal("(inventory.rating < 12.25)", DocumentCondition.For<Inventory>(p => p.Rating < threshold));
            Assert.Equal("(inventory.revision == 9223372036854775807)", DocumentCondition.For<Inventory>(p => p.Revision == long.MaxValue));
            Assert.Equal("(inventory.unit_price == 79228162514264337593543950335.0)", DocumentCondition.For<Inventory>(p => p.Price == decimal.MaxValue));
        }
        finally
        {
            CultureInfo.CurrentCulture = previous;
        }
    }

    [Fact]
    public void NumericLiterals_PreserveFloatValuesAndNativeParserSyntax()
    {
        var single = 0.1f;
        var minimum = long.MinValue;
        Assert.Equal("(inventory.single_value == 0.10000000149011612)", DocumentCondition.For<Inventory>(p => p.SingleValue == single));
        Assert.Equal("(inventory.rating == 1.0E-12)", DocumentCondition.For<Inventory>(p => p.Rating == 1e-12));
        Assert.Equal("(inventory.rating == 1.0E+100)", DocumentCondition.For<Inventory>(p => p.Rating == 1e100));
        Assert.Equal("(inventory.revision > 0x8000000000000000)", DocumentCondition.For<Inventory>(p => p.Revision > minimum));
    }

    [Fact]
    public void IntegerFields_AreNotSilentlyPromotedToDecimal()
    {
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Quantity == 1.0000000000000000000000000001m));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => (decimal)p.Revision == 9007199254740992.1m));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => (decimal)(long)p.Quantity > 0));
        var quantity = 1;
        Assert.Equal("(inventory.unit_price > 1)", DocumentCondition.For<Inventory>(p => p.Price > quantity));
        var first = 1m;
        var second = 1.0000000000000000000000000001m;
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => first == second));
    }

    [Fact]
    public void CapturedNullableOrdering_RendersCurrentNullAndPresentValues()
    {
        int? limit = null;
        Expression<Func<Inventory, bool>> predicate = p => !(p.OptionalQuantity < limit);
        Assert.Equal("not (false)", DocumentCondition.For(predicate));
        limit = 5;
        Assert.Equal("not ((inventory.optional_quantity != null and (inventory.optional_quantity < 5)))", DocumentCondition.For(predicate));
    }

    [Fact]
    public void StringValues_UseSelectorByteEscapesAndExactEquality()
    {
        var value = "\"\\\n\r\t\f\0café 日本 😀*?";
        Assert.Equal("(inventory.display_name == \"\\\"\\\\\\n\\r\\t\\f\\x00caf\\xC3\\xA9 \\xE6\\x97\\xA5\\xE6\\x9C\\xAC \\xF0\\x9F\\x98\\x80*?\")",
            DocumentCondition.For<Inventory>(p => p.Name == value));
    }

    [Fact]
    public void NullChecks_AndNullableOrdering_PreserveNegation()
    {
        Assert.Equal("(inventory.display_name == null)", DocumentCondition.For<Inventory>(p => p.Name == null));
        Assert.Equal("(inventory.optional_quantity != null)", DocumentCondition.For<Inventory>(p => p.OptionalQuantity.HasValue));
        Assert.Equal("not ((inventory.optional_quantity != null and (inventory.optional_quantity < 5)))",
            DocumentCondition.For<Inventory>(p => !(p.OptionalQuantity < 5)));
        Assert.Equal("(inventory.tags != null)", DocumentCondition.For<Inventory>(p => p.Tags != null));
    }

    [Fact]
    public void FieldComparisons_AndBooleanConstants_AreSupported()
    {
        Assert.Equal("(inventory.revision > inventory.quantity)",
            DocumentCondition.For<Inventory>(p => p.Revision > p.Quantity));
        Assert.Equal("true", DocumentCondition.For<Inventory>(p => true));
        Assert.Equal("false", DocumentCondition.For<Inventory>(p => false));
        var enabled = true;
        Assert.Equal("true", DocumentCondition.For<Inventory>(p => enabled));
    }

    [Fact]
    public void CapturedValues_AreReadWhenBuildingEachCondition()
    {
        var revision = 1L;
        Expression<Func<Inventory, bool>> predicate = p => p.Revision == revision;
        Assert.Equal("(inventory.revision == 1)", DocumentCondition.For(predicate));
        revision = 2;
        Assert.Equal("(inventory.revision == 2)", DocumentCondition.For(predicate));
    }

    [Fact]
    public void UnsupportedExpressions_DoNotExecuteUserCode()
    {
        var probe = new SideEffects();
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Name == probe.Value));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Name == probe.GetValue()));
        Assert.Equal(0, probe.Calls);
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Name!.Contains("test")));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.OptionalQuantity!.Value > 0));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Quantity + 1 > 0));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.IsActive & p.IsActive));
    }

    [Fact]
    public void LossyConversions_AndNonFiniteNumbers_AreRejected()
    {
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => (int)p.Rating > 0));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => (double)p.Revision > 0));
        var nan = double.NaN;
        var infinity = double.PositiveInfinity;
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Rating == nan));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For<Inventory>(p => p.Rating < infinity));
    }

    [Fact]
    public void InvalidMetadata_AndMissingModelMetadata_AreRejected()
    {
        Assert.Throws<ArgumentException>(() => DocumentCondition.For<BadField>(p => p.Name == "test"));
        Assert.Throws<ArgumentException>(() => DocumentCondition.For<BadDocument>(p => p.Name == "test"));
        Assert.Throws<InvalidOperationException>(() => DocumentCondition.For<Unmapped>(p => p.Name == "test"));
        Assert.Throws<ArgumentNullException>(() => DocumentCondition.For<Inventory>(null!));
    }

    [Fact]
    public void ReservedFieldNames_AreRejectedButIdIsAllowed()
    {
        Assert.Throws<ArgumentException>(() => DocumentCondition.For<KeywordFields>(p => p.Reserved > 0));
        Assert.Equal("(inventory.id == \"item\")", DocumentCondition.For<KeywordFields>(p => p.Identifier == "item"));
    }

    [Fact]
    public void HandBuiltStringExpressions_DoNotChangeOperatorSemantics()
    {
        var parameter = Expression.Parameter(typeof(Inventory), "p");
        var name = Expression.Property(parameter, nameof(Inventory.Name));
        var value = Expression.Constant("value", typeof(string));
        var identity = Expression.Lambda<Func<Inventory, bool>>(Expression.ReferenceEqual(name, value), parameter);
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For(identity));
        var inequality = typeof(string).GetMethod("op_Inequality", [typeof(string), typeof(string)])!;
        var mismatched = Expression.Lambda<Func<Inventory, bool>>(Expression.Equal(name, value, false, inequality), parameter);
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For(mismatched));
    }

    [Fact]
    public void IdentityConversions_DoNotInvokeCapturedConvertibleObjects()
    {
        var probe = new Mock<IConvertible>(MockBehavior.Strict);
        var captured = Expression.Constant(probe.Object, typeof(IConvertible));
        var identity = Expression.Convert(captured, typeof(IConvertible));
        var body = Expression.Equal(identity, Expression.Constant(null, typeof(IConvertible)));
        var condition = Expression.Lambda<Func<Inventory, bool>>(body, Expression.Parameter(typeof(Inventory), "p"));
        Assert.Throws<NotSupportedException>(() => DocumentCondition.For(condition));
        probe.VerifyNoOtherCalls();
    }

    [VespaDocument("inventory", Namespace = "tenant")]
    private sealed class Inventory
    {
        [VespaField(Name = "unit_price")] public decimal Price { get; init; }
        [VespaField(Name = "display_name")] public string? Name { get; init; }
        public bool IsActive { get; init; }
        public double Rating { get; init; }
        public float SingleValue { get; init; }
        public long Revision { get; init; }
        public int Quantity { get; init; }
        public int? OptionalQuantity { get; init; }
        public string[]? Tags { get; init; }
    }

    [VespaDocument("inventory")]
    private sealed class BadField
    {
        [VespaField(Name = "name or true")] public string Name { get; init; } = "";
    }

    [VespaDocument("inventory or true")]
    private sealed class BadDocument { public string Name { get; init; } = ""; }

    [VespaDocument("inventory")]
    private sealed class KeywordFields
    {
        [VespaField(Name = "TRUE")] public int Reserved { get; init; }
        [VespaField(Name = "id")] public string Identifier { get; init; } = "";
    }

    private sealed class Unmapped { public string Name { get; init; } = ""; }

    private sealed class SideEffects
    {
        public int Calls { get; private set; }
        public string Value { get { Calls++; return "value"; } }
        public string GetValue() { Calls++; return "value"; }
    }
}

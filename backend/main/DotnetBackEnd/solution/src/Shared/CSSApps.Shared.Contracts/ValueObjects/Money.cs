#pragma warning disable // Disables all warnings for this file

using System.Diagnostics;
using CSSApps.Shared.Contracts.Primitives;

namespace CSSApps.Shared.Contracts.ValueObjects;

public class Currency : ValueObject
{
    public required string Code { get; init; }
    public required string Symbol { get; init; }
    public byte DecimalPlaces { get; init; }

    public static Currency USD => new() { Code = "USD", Symbol = "$", DecimalPlaces = 2 };
    public static Currency EUR => new() { Code = "EUR", Symbol = "€", DecimalPlaces = 2 };

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Code;
    }
}

public class Money : ValueObject
{
    private Money()
    {
    } // EF Core

    private Money(decimal amount, Currency currency)
    {
        Amount = decimal.Round(amount, currency.DecimalPlaces);
        Currency = currency;
    }

    private decimal Amount { get; init; }
    private Currency Currency { get; init; } = null!;

    public static Result<Money> Create(decimal amount, Currency currency)
    {
        Debug.Assert(currency != null, nameof(currency) + " != null");
        return new Money(amount, currency);
    }

    public static Money Zero(Currency currency)
    {
        return new Money(0, currency);
    }

    // Arithmetic operations
    public Money Add(Money other)
    {
        Debug.Assert(other != null, nameof(other) + " != null");
        EnsureSameCurrency(other);
        return new Money(Amount + other.Amount, Currency);
    }

    public Money Subtract(Money other)
    {
        ArgumentNullException.ThrowIfNull(other);
        EnsureSameCurrency(other);
        return new Money(Amount - other.Amount, Currency);
    }

    public Money Multiply(decimal multiplier)
    {
        return new Money(Amount * multiplier, Currency);
    }

    public Money Divide(decimal divisor)
    {
        if (divisor == 0)
            throw new DivideByZeroException();
        return new Money(Amount / divisor, Currency);
    }

    // Comparison
    private int CompareTo(Money? other)
    {
        if (other == null) return 1;
        EnsureSameCurrency(other);
        return Amount.CompareTo(other.Amount);
    }

    // Formatting
    public string ToString(bool includeSymbol = true)
    {
        return includeSymbol
            ? $"{Currency.Symbol}{Amount.ToString($"N{Currency.DecimalPlaces}")}"
            : Amount.ToString($"N{Currency.DecimalPlaces}");
    }

    // Operators
    public static Money operator +(Money left, Money right)
    {
        return left.Add(right);
    }

    public static Money operator -(Money left, Money right)
    {
        return left.Subtract(right);
    }

    public static Money operator *(Money left, decimal right)
    {
        return left.Multiply(right);
    }

    public static Money operator /(Money left, decimal right)
    {
        return left.Divide(right);
    }

    public static bool operator >(Money left, Money right)
    {
        return left.CompareTo(right) > 0;
    }

    public static bool operator <(Money left, Money right)
    {
        return left.CompareTo(right) < 0;
    }

    public static bool operator >=(Money left, Money right)
    {
        return left.CompareTo(right) >= 0;
    }

    public static bool operator <=(Money left, Money right)
    {
        return left.CompareTo(right) <= 0;
    }

    protected override IEnumerable<object> GetEqualityComponents()
    {
        yield return Amount;
        yield return Currency;
    }

    private void EnsureSameCurrency(Money other)
    {
        if (other.Currency.Code != Currency.Code)
            throw new InvalidOperationException(
                $"Cannot operate on money with different currencies ({Currency.Code} vs {other.Currency.Code})");
    }
}
namespace Ago.Chat.Domain.Tests;

public class PhoneNumberTests
{
    [Fact]
    public void Constructor_WithSeparatorsAndParens_NormalizesToE164()
    {
        var phone = new PhoneNumber("+7 (999) 123-45-67");

        Assert.Equal("+79991234567", phone.Value);
    }

    [Fact]
    public void Constructor_AlreadyCanonical_RoundTrips()
    {
        var phone = new PhoneNumber("+79991234567");

        Assert.Equal("+79991234567", phone.Value);
    }

    [Fact]
    public void Constructor_TwoDifferentlyFormattedInputs_ProduceEqualValues()
    {
        var a = new PhoneNumber("+7 999 123-45-67");
        var b = new PhoneNumber("+79991234567");

        Assert.Equal(a, b);
    }

    [Fact]
    public void Constructor_WithLeadingZero_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PhoneNumber("+07991234567"));
    }

    [Fact]
    public void Constructor_TooFewDigits_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PhoneNumber("+1234567"));
    }

    [Fact]
    public void Constructor_TooManyDigits_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PhoneNumber("+1234567890123456"));
    }

    [Fact]
    public void Constructor_WithLetters_Throws()
    {
        Assert.Throws<ArgumentException>(() => new PhoneNumber("+7999CALLNOW"));
    }

    [Fact]
    public void Constructor_Null_Throws()
    {
        Assert.Throws<ArgumentNullException>(() => new PhoneNumber(null!));
    }
}

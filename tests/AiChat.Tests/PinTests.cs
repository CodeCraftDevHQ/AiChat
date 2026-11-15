using AiChat.Services;

namespace AiChat.Tests;

public class PinTests
{
    [Fact]
    public void Correct_pin_verifies_and_wrong_does_not()
    {
        var salt = PinHelper.NewSalt();
        var hash = PinHelper.Hash("1234", salt);
        Assert.True(PinHelper.Verify("1234", salt, hash));
        Assert.False(PinHelper.Verify("1235", salt, hash));
        Assert.False(PinHelper.Verify("", salt, hash));
    }

    [Fact]
    public void Salt_changes_the_hash_and_pin_is_not_in_it()
    {
        var a = PinHelper.Hash("1234", PinHelper.NewSalt());
        var b = PinHelper.Hash("1234", PinHelper.NewSalt());
        Assert.NotEqual(a, b);
        Assert.DoesNotContain("1234", a);
    }

    [Fact]
    public void Missing_stored_values_never_verify()
    {
        Assert.False(PinHelper.Verify("1234", "", ""));
    }
}

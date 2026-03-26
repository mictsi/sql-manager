namespace SqlManager.Tests;

public sealed class PasswordGeneratorTests
{
    [Fact]
    public void Generate_DefaultLength_ReturnsPasswordWithAllRequiredCharacterGroups()
    {
        var generator = new PasswordGenerator();

        var password = generator.Generate();

        Assert.Equal(20, password.Length);
        Assert.Contains(password, character => char.IsUpper(character));
        Assert.Contains(password, character => char.IsLower(character));
        Assert.Contains(password, character => char.IsDigit(character));
        Assert.Contains(password, character => !char.IsLetterOrDigit(character));
    }

    [Fact]
    public void Generate_LengthBelowMinimum_ThrowsUserInputException()
    {
        var generator = new PasswordGenerator();

        var exception = Assert.Throws<UserInputException>(() => generator.Generate(14));

        Assert.Equal("Password length must be at least 15 characters.", exception.Message);
    }
}
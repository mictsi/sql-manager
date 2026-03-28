using System.Security.Cryptography;

namespace SqlManager;

internal sealed class PasswordGenerator
{
    private const string Upper = "ABCDEFGHJKLMNPQRSTUVWXYZ";
    private const string Lower = "abcdefghijkmnopqrstuvwxyz";
    private const string Digits = "23456789";
    private const string Special = "!@#$:./?=+-_()";

    public string Generate(int length = 20)
    {
        if (length < 15)
        {
            throw new UserInputException("Password length must be at least 15 characters.");
        }

        var required = new[]
        {
            Pick(Upper),
            Pick(Lower),
            Pick(Digits),
            Pick(Special)
        };

        var all = Upper + Lower + Digits + Special;
        var characters = new List<char>(required);
        while (characters.Count < length)
        {
            characters.Add(Pick(all));
        }

        for (var index = characters.Count - 1; index > 0; index--)
        {
            var swapIndex = RandomNumberGenerator.GetInt32(index + 1);
            (characters[index], characters[swapIndex]) = (characters[swapIndex], characters[index]);
        }

        return new string(characters.ToArray());
    }

    private static char Pick(string source)
        => source[RandomNumberGenerator.GetInt32(source.Length)];
}

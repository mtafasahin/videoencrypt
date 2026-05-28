using System.Text;

namespace MtVid;

internal static class PasswordPrompt
{
    public static string Read(string prompt)
    {
        Console.Write(prompt);
        StringBuilder builder = new();

        ConsoleKeyInfo key;
        while ((key = Console.ReadKey(intercept: true)).Key != ConsoleKey.Enter)
        {
            if (key.Key == ConsoleKey.Backspace)
            {
                if (builder.Length > 0)
                {
                    builder.Length -= 1;
                }
                continue;
            }

            if (!char.IsControl(key.KeyChar))
            {
                builder.Append(key.KeyChar);
            }
        }

        Console.WriteLine();
        return builder.ToString();
    }
}

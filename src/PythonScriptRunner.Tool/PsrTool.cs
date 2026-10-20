using System.Reflection;
using System.Text;
using Microsoft.AspNetCore.Identity;

namespace PythonScriptRunner.Tool;

/// <summary>The <c>psr</c> command (spec 009; D-124, D-130). Today: <c>psr hash-password</c>.</summary>
public static class PsrTool
{
    /// <summary>Entry point.</summary>
    /// <param name="args">Command line.</param>
    /// <returns>0 on success.</returns>
    public static int Main(string[] args) => Run(args, ToolConsole.System);

    internal static int Run(IReadOnlyList<string> args, ToolConsole console)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(console);
        switch (args.Count == 0 ? null : args[0])
        {
            case "hash-password":
                return HashPassword([.. args.Skip(1)], console);
            case "-h" or "--help" or "help":
                Usage(console.Out);
                return 0;
            case "--version":
                console.Out.WriteLine(typeof(PsrTool).Assembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?.InformationalVersion);
                return 0;
            case null:
                Usage(console.Error);
                return 1;
            default:
                console.Error.WriteLine($"Unknown command '{args[0]}'.");
                Usage(console.Error);
                return 1;
        }
    }

    /// <summary>
    /// Prints the PBKDF2 hash (ASP.NET Identity v3 format) of a password for <c>PasswordHash</c> of a fixed login user (D-121).
    /// The password is typed twice without echo, or read from the first line of the standard input with <c>--stdin</c>; it is
    /// never printed.
    /// </summary>
    private static int HashPassword(IReadOnlyList<string> options, ToolConsole console)
    {
        var unknown = options.Where(o => o != "--stdin").ToList();
        if (unknown.Count > 0)
        {
            console.Error.WriteLine($"Unknown option '{unknown[0]}'. The password is never an argument: type it or pipe it with --stdin.");
            return 1;
        }

        string? password;
        if (options.Contains("--stdin"))
        {
            password = console.In.ReadLine();
        }
        else if (console.IsInputRedirected)
        {
            console.Error.WriteLine("The input is redirected: use 'psr hash-password --stdin' to read the password from it.");
            return 1;
        }
        else
        {
            console.Error.Write("Password: ");
            password = console.ReadSecret();
            console.Error.Write("Repeat the password: ");
            if (console.ReadSecret() != password)
            {
                console.Error.WriteLine("The passwords do not match.");
                return 1;
            }
        }

        if (string.IsNullOrEmpty(password))
        {
            console.Error.WriteLine("The password is empty.");
            return 1;
        }

        console.Out.WriteLine(new PasswordHasher<object>().HashPassword(new object(), password));
        return 0;
    }

    private static void Usage(TextWriter writer)
    {
        writer.WriteLine("Usage: psr <command>");
        writer.WriteLine();
        writer.WriteLine("Commands:");
        writer.WriteLine("  hash-password [--stdin]   Prints the hash of a password for PythonScriptRunner__Dashboard__Login__Users__N__PasswordHash.");
        writer.WriteLine("                            The password is typed twice without echo, or read from the first line of the input with --stdin.");
    }
}

/// <summary>Console of the tool (replaced in tests).</summary>
/// <param name="In">Standard input.</param>
/// <param name="Out">Standard output (only results).</param>
/// <param name="Error">Prompts and errors.</param>
/// <param name="IsInputRedirected">Whether the input is not a terminal.</param>
/// <param name="ReadSecret">Reads a line without echo.</param>
internal sealed record ToolConsole(TextReader In, TextWriter Out, TextWriter Error, bool IsInputRedirected, Func<string> ReadSecret)
{
    public static ToolConsole System { get; } = new(Console.In, Console.Out, Console.Error, Console.IsInputRedirected, ReadWithoutEcho);

    private static string ReadWithoutEcho()
    {
        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            switch (key.Key)
            {
                case ConsoleKey.Enter:
                    Console.Error.WriteLine();
                    return text.ToString();
                case ConsoleKey.Backspace:
                    if (text.Length > 0)
                    {
                        text.Length--;
                    }

                    break;
                default:
                    if (!char.IsControl(key.KeyChar))
                    {
                        text.Append(key.KeyChar);
                    }

                    break;
            }
        }
    }
}

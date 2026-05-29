using System.Globalization;
using System.Diagnostics;

namespace MtVid;

internal static class Program
{
	public static int Main(string[] args)
	{
		if (args.Length == 0 || IsHelpCommand(args[0]))
		{
			PrintHelp();
			return 0;
		}

		string command = args[0].ToLowerInvariant();
		Dictionary<string, string> options = ParseOptions(args.Skip(1).ToArray());

		try
		{
			return command switch
			{
				"pack" => RunPack(options),
				"serve" => RunServe(options),
				_ => Fail($"Unknown command: {command}")
			};
		}
		catch (Exception ex)
		{
			return Fail(ex.Message);
		}
	}

	private static int RunPack(IReadOnlyDictionary<string, string> options)
	{
		string inputPath = RequireOption(options, "input", "i");
		string outputPath = RequireOption(options, "output", "o");
		int chunkSizeMb = ParseIntOption(options, 2, "chunk-mb");
		int iterations = ParseIntOption(options, 210000, "iterations");
		string contentType = GetOption(options, "content-type") ?? GuessContentType(inputPath);

		if (chunkSizeMb <= 0)
		{
			return Fail("--chunk-mb must be greater than 0.");
		}

		if (iterations < 50000)
		{
			return Fail("--iterations must be at least 50000.");
		}

		string password = GetOption(options, "password", "p") ?? PasswordPrompt.Read("Package password: ");
		int chunkSizeBytes = checked(chunkSizeMb * 1024 * 1024);

		VideoPackager.EncryptVideo(inputPath, outputPath, password, chunkSizeBytes, contentType, iterations, originalFileName: Path.GetFileName(inputPath));
		Console.WriteLine($"Package created: {outputPath}");
		return 0;
	}

	private static int RunServe(IReadOnlyDictionary<string, string> options)
	{
		string? inputPath = GetOption(options, "input", "i");
		int port = ParseIntOption(options, 8080, "port");
		bool enableUi = ParseBoolOption(options, false, "ui");
		bool openBrowser = ParseBoolOption(options, false, "open");
		if (port is <= 0 or > 65535)
		{
			return Fail("--port must be in range 1-65535.");
		}

		if (string.IsNullOrWhiteSpace(inputPath) && !enableUi)
		{
			return Fail("--input is required unless --ui true is set.");
		}

		string? password = null;
		if (!string.IsNullOrWhiteSpace(inputPath))
		{
			password = GetOption(options, "password", "p") ?? PasswordPrompt.Read("Playback password: ");
		}

		using LocalVideoServer server = new(inputPath, password, port, enableUi);
		server.Start();

		if (!string.IsNullOrWhiteSpace(inputPath))
		{
			Console.WriteLine($"Streaming URL: http://localhost:{port}/stream");
		}
		else
		{
			Console.WriteLine("No package preloaded. Use Player UI to open a .mtaf file.");
		}
		if (enableUi)
		{
			string uiUrl = $"http://localhost:{port}/";
			Console.WriteLine($"Player UI URL: {uiUrl}");
			if (openBrowser)
			{
				TryOpenBrowser(uiUrl);
			}
		}
		Console.WriteLine("Press ENTER to stop.");
		Console.ReadLine();
		return 0;
	}

	private static Dictionary<string, string> ParseOptions(string[] args)
	{
		Dictionary<string, string> options = new(StringComparer.OrdinalIgnoreCase);
		for (int i = 0; i < args.Length; i++)
		{
			string current = args[i];
			if (!current.StartsWith('-'))
			{
				throw new ArgumentException($"Unexpected argument: {current}");
			}

			string key = current.TrimStart('-');
			if (string.IsNullOrWhiteSpace(key))
			{
				throw new ArgumentException("Invalid option key.");
			}

			if (i + 1 >= args.Length || args[i + 1].StartsWith('-'))
			{
				throw new ArgumentException($"Option '{current}' requires a value.");
			}

			options[key] = args[i + 1];
			i++;
		}

		return options;
	}

	private static string RequireOption(IReadOnlyDictionary<string, string> options, params string[] keys)
	{
		string? value = GetOption(options, keys);
		if (string.IsNullOrWhiteSpace(value))
		{
			string keyList = string.Join(", ", keys.Select(static k => "--" + k));
			throw new ArgumentException($"Missing required option: {keyList}");
		}

		return value;
	}

	private static string? GetOption(IReadOnlyDictionary<string, string> options, params string[] keys)
	{
		foreach (string key in keys)
		{
			if (options.TryGetValue(key, out string? value))
			{
				return value;
			}
		}

		return null;
	}

	private static int ParseIntOption(IReadOnlyDictionary<string, string> options, int defaultValue, params string[] keys)
	{
		string? value = GetOption(options, keys);
		if (value is null)
		{
			return defaultValue;
		}

		if (!int.TryParse(value, NumberStyles.Integer, CultureInfo.InvariantCulture, out int parsed))
		{
			throw new ArgumentException($"Invalid integer for option '{keys[0]}': {value}");
		}

		return parsed;
	}

	private static bool ParseBoolOption(IReadOnlyDictionary<string, string> options, bool defaultValue, params string[] keys)
	{
		string? value = GetOption(options, keys);
		if (value is null)
		{
			return defaultValue;
		}

		if (bool.TryParse(value, out bool parsed))
		{
			return parsed;
		}

		if (value.Equals("1", StringComparison.OrdinalIgnoreCase) || value.Equals("yes", StringComparison.OrdinalIgnoreCase))
		{
			return true;
		}

		if (value.Equals("0", StringComparison.OrdinalIgnoreCase) || value.Equals("no", StringComparison.OrdinalIgnoreCase))
		{
			return false;
		}

		throw new ArgumentException($"Invalid boolean for option '{keys[0]}': {value}");
	}

	private static bool IsHelpCommand(string command)
	{
		string normalized = command.ToLowerInvariant();
		return normalized is "help" or "--help" or "-h";
	}

	private static string GuessContentType(string inputPath)
	{
		string extension = Path.GetExtension(inputPath).ToLowerInvariant();
		return extension switch
		{
			".mp4" => "video/mp4",
			".webm" => "video/webm",
			".mkv" => "video/x-matroska",
			".mov" => "video/quicktime",
			_ => "application/octet-stream"
		};
	}

	private static void PrintHelp()
	{
		Console.WriteLine("MtVid - Encrypted video package tool");
		Console.WriteLine();
		Console.WriteLine("Commands:");
		Console.WriteLine("  pack  --input <video> --output <file.mtaf> [--password <pwd>] [--chunk-mb 2] [--content-type video/mp4] [--iterations 210000]");
		Console.WriteLine("  serve [--input <file.mtaf>] [--password <pwd>] [--port 8080] [--ui true] [--open true]");
		Console.WriteLine();
		Console.WriteLine("Example:");
		Console.WriteLine("  dotnet run --project MtVid -- pack --input sample.mp4 --output sample.mtaf --chunk-mb 2");
		Console.WriteLine("  dotnet run --project MtVid -- serve --input sample.mtaf --ui true --open true");
	}

	private static void TryOpenBrowser(string url)
	{
		try
		{
			Process.Start(new ProcessStartInfo
			{
				FileName = url,
				UseShellExecute = true
			});
		}
		catch
		{
			Console.WriteLine("Browser could not be opened automatically. Open the Player UI URL manually.");
		}
	}

	private static int Fail(string message)
	{
		Console.Error.WriteLine($"Error: {message}");
		return 1;
	}
}

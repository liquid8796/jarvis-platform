using System.IO;

namespace JarvisCode.App.Services;

/// <summary>
/// The words the user told the spell checker to accept — the reference's "Add to
/// dictionary" (its <c>addWordToSpellCheckerDictionary</c>).
///
/// WPF reads custom dictionaries as .lex files handed to
/// <c>SpellCheck.CustomDictionaries</c>, so the store is one such file inside the
/// profile: a plain UTF-8 word list under a <c>#LID</c> header, which is the format
/// that loader accepts.
/// </summary>
public sealed class CustomDictionary
{
    /// <summary>The header .lex files carry; 0 means the file applies to every language.</summary>
    private const string Header = "#LID 0";

    private readonly string _path;
    private readonly List<string> _words = [];

    public CustomDictionary(string profileRoot)
    {
        _path = Path.Combine(profileRoot, "custom-dictionary.lex");
        Load();
    }

    /// <summary>The file to hand to <c>SpellCheck.CustomDictionaries</c>.</summary>
    public Uri Uri => new(_path);

    public IReadOnlyList<string> Words => _words;

    /// <summary>
    /// Adds a word and writes the file. Returns false when the word was already
    /// there or is not a word, so a caller can skip re-attaching the dictionary.
    /// </summary>
    public bool Add(string word)
    {
        word = word.Trim();
        if (word.Length == 0 || word.Contains('\n') || _words.Contains(word, StringComparer.Ordinal))
        {
            return false;
        }

        _words.Add(word);
        Save();
        return true;
    }

    private void Load()
    {
        try
        {
            if (!File.Exists(_path))
            {
                Save();
                return;
            }

            foreach (var line in File.ReadAllLines(_path))
            {
                var word = line.Trim();
                if (word.Length > 0 && !word.StartsWith('#'))
                {
                    _words.Add(word);
                }
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A dictionary that cannot be read is an empty one.
        }
    }

    private void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            File.WriteAllLines(_path, [Header, .. _words], new System.Text.UTF8Encoding(true));
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // The word is still accepted for this session's in-memory list.
        }
    }
}

using System.Runtime.CompilerServices;
using Lumen.Core.Chat;
using Lumen.Core.Llm;
using Lumen.Core.Settings;

namespace Lumen.Core.Tests;

/// <summary>A scriptable backend: it can be unavailable, fail before output, or fail midway.</summary>
internal sealed class FakeBackend : ILlmBackend
{
    public FakeBackend(BackendKind kind, params string[] pieces)
    {
        Kind = kind;
        Pieces = pieces;
    }

    public BackendKind Kind { get; }

    public string DisplayName => $"fake-{Kind}";

    public string[] Pieces { get; set; }

    public string? UnavailableReason { get; set; }

    /// <summary>Throw after this many pieces (0 = before any output). Null = never fail.</summary>
    public int? FailAfter { get; set; }

    public bool FailureCanFallBack { get; set; } = true;

    public int Calls { get; private set; }

    public BackendAvailability CheckAvailability(Conversation conversation) =>
        UnavailableReason is null ? BackendAvailability.Available : BackendAvailability.Unavailable(UnavailableReason);

    public async IAsyncEnumerable<string> StreamAsync(Conversation conversation, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        Calls++;
        for (int i = 0; i < Pieces.Length; i++)
        {
            if (FailAfter == i)
            {
                throw new LlmBackendException(Kind, $"{Kind} broke", FailureCanFallBack);
            }

            cancellationToken.ThrowIfCancellationRequested();
            await Task.Yield();
            yield return Pieces[i];
        }

        if (FailAfter == Pieces.Length)
        {
            throw new LlmBackendException(Kind, $"{Kind} broke", FailureCanFallBack);
        }
    }
}

/// <summary>Settings that tests can change between calls.</summary>
internal sealed class TestSettings : ISettingsProvider
{
    public AppSettings Current { get; set; } = new();

    public event EventHandler<AppSettings>? Changed;

    public void Raise() => Changed?.Invoke(this, Current);
}

/// <summary>"Encryption" that is easy to inspect in tests.</summary>
internal sealed class ReversingProtector : ISecretProtector
{
    public string Protect(string plainText) => "enc:" + new string(plainText.Reverse().ToArray());

    public string? Unprotect(string protectedText) =>
        protectedText.StartsWith("enc:", StringComparison.Ordinal) ? new string(protectedText[4..].Reverse().ToArray()) : null;
}

namespace MergeVerifier;

// Messages are authored locally, never constructed from API bodies, credentials or user JSON.
public sealed class VerifierException(string message) : Exception(message);

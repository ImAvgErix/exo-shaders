namespace SCSKiller.Core.Planning;

public readonly record struct Resolved<T>(T Value, Provenance Provenance);

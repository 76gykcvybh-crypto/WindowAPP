namespace VfdWpfApp.Models;

public sealed record ParameterDefinition(
    string AddressHex,
    int Address,
    string Name,
    string Access,
    string? Unit,
    string Description,
    string? Note,
    IReadOnlyList<BitFieldDefinition> Bitfields
);

public sealed record BitFieldDefinition(int Bit, string Meaning);

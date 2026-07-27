from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

contracts = ROOT / "src/BookStudio.Application/OpenCode/AgentToolProfileContracts.cs"
text = contracts.read_text(encoding="utf-8")
old = '''    public override bool Equals(object? obj) => Equals(obj as EffectiveAgentToolProfile);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
'''
new = '''    public override bool Equals(object? obj) => Equals(obj as EffectiveAgentToolProfile);

    public static bool operator ==(
        EffectiveAgentToolProfile? left,
        EffectiveAgentToolProfile? right) =>
        ReferenceEquals(left, right) || (left?.Equals(right) ?? false);

    public static bool operator !=(
        EffectiveAgentToolProfile? left,
        EffectiveAgentToolProfile? right) =>
        !(left == right);

    public override int GetHashCode() => StringComparer.Ordinal.GetHashCode(Fingerprint);
'''
if old in text:
    contracts.write_text(text.replace(old, new, 1), encoding="utf-8")
elif "public static bool operator ==" not in text:
    raise SystemExit("Effective profile equality anchor missing")

journey = ROOT / "tests/BookStudio.Tests.AgentToolProfiles/AgentToolProfilesJourney.cs"
text = journey.read_text(encoding="utf-8")
old = '''internal sealed class AgentToolProfilesJourney
{
    private int _scenarios;
'''
new = '''internal sealed class AgentToolProfilesJourney
{
    private const string MutationGateMarker = "mutation=NONE";

    private int _scenarios;
'''
if old in text:
    journey.write_text(text.replace(old, new, 1), encoding="utf-8")
elif 'MutationGateMarker = "mutation=NONE"' not in text:
    raise SystemExit("Journey marker anchor missing")

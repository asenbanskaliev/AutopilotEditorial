from pathlib import Path

ROOT = Path(__file__).resolve().parents[1]

contracts = ROOT / "src/BookStudio.Application/OpenCode/AgentToolProfileContracts.cs"
text = contracts.read_text(encoding="utf-8")
text = text.replace(
    "    public EffectiveAgentToolProfile(\n",
    "    internal EffectiveAgentToolProfile(\n",
    1,
)
contracts.write_text(text, encoding="utf-8")

resolver = ROOT / "src/BookStudio.Application/OpenCode/AgentToolProfileResolver.cs"
text = resolver.read_text(encoding="utf-8")
text = text.replace(
    "    public static string Compute(EffectiveAgentToolProfile profile)\n",
    "    internal static string Compute(EffectiveAgentToolProfile profile)\n",
    1,
)
resolver.write_text(text, encoding="utf-8")

mapper = ROOT / "src/BookStudio.OpenCode/OpenCodeAgentToolProfileMapper.cs"
text = mapper.read_text(encoding="utf-8")
text = text.replace(
    "    public OpenCodeMappedAgentToolProfile(\n",
    "    internal OpenCodeMappedAgentToolProfile(\n",
    1,
)
mapper.write_text(text, encoding="utf-8")

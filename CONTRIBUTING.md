# Contributing

Issues and pull requests are welcome. Please keep contributions platform-neutral where practical while preserving Ubuntu as the first supported deployment target.

Before opening a pull request:

```bash
dotnet build --configuration Release
dotnet run --project tests/Bloodraven.Tests --configuration Release
python3 -m unittest discover -s tests -p 'test_*.py' -v
bash -n install.sh upgrade.sh scripts/install.sh scripts/upgrade.sh
```

Do not include bot tokens, Codex credentials, internal hostnames, IP addresses, or other private infrastructure details in issues, logs, fixtures, or commits.

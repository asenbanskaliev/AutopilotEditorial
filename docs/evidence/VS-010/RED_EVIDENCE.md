# VS-010 — Dual Red Evidence

## RED-I

GitHub Actions `Governance Gates` run `30209623728`, job `89813554535`, failed in the governance test step after plan integrity, completion policy and CI-provider validation had passed.

Expected missing behavior:

- no `global.json`;
- no `BookStudio.slnx`;
- no central build/package props;
- no required .NET projects;
- no project-reference graph.

## RED-E

No `.NET CI` run was created for the RED commit because the pull request contained no `.cs`, `.csproj`, `.slnx` or central .NET build files. Therefore a clean-checkout restore/build/architecture-test journey did not yet exist.

## Note

The evidence-upload step in the governance workflow failed after the deliberate test failure because no evidence JSON had been generated. This will be corrected separately so `if: always()` does not turn an expected RED into a second misleading failure.

## Confirmation

- the plan and provider contracts were healthy;
- the RED failure was caused by the absent solution baseline;
- no .NET build result was fabricated.

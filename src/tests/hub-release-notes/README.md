# Release-note formatting checks

Run on Windows with the .NET 10 Windows Desktop SDK:

```powershell
dotnet run --project src/tests/hub-release-notes/HubReleaseNotesTests.csproj -c Release
```

An optional argument can point to the exact changelog used for a release. Without
one, the suite finds `src/RELEASE_NOTES_V3.0.1.md` in the checkout.

The suite checks readable document output, native text styles, code/path literals,
Unicode and size bounds, link targets and explicit activation, and clearing old
targets on refresh. It creates only a hidden native RichTextBox handle. It does
not show the Hub, open a browser, fetch remote images, contact GitHub, download an
update, or modify installed settings.

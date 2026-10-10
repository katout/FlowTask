// The analyzers' titles, messages and code fix titles follow the UI culture (English, and Japanese on a Japanese
// system; docs/en/tools/analyzers.md). Tests that read them compare English text, so they run in English on every machine.
// A test that checks another language sets its own culture (LocalizationTests).
[assembly: SetUICulture("en-US")]

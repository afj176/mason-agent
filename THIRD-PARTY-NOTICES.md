# Third-party notices

C# code navigation uses Microsoft.CodeAnalysis.CSharp and Microsoft.CodeAnalysis.Common (Roslyn), distributed by Microsoft under the MIT license.

Headings and the app name use Manrope under the SIL Open Font License 1.1. Its license is included in licenses/LICENSE-Manrope.txt in application releases and windows/src/MasonAgent/Assets/Fonts/LICENSE-Manrope.txt and site/assets/LICENSE-Manrope.txt in source. Native controls use Windows system fonts.

The application uses Microsoft .NET and Windows App SDK. Their bundled license and third-party notices remain in the release payload. Local model runtimes and model weights are configured separately and retain their own licenses; weights are not bundled.

The Hugging Face logo shown beside model search and downloads is a trademark of Hugging Face, Inc., used only to identify that service.

Browser verification uses Microsoft Playwright under the Apache License 2.0. Releases include the Playwright license and third-party notices in `.playwright/package` and the bundled Node.js license in `.playwright/node/LICENSE`. Microsoft Edge is supplied by the Windows installation.
# Language analysis dependencies

Mason ships the TypeScript compiler under Apache-2.0, Jedi under MIT, and Parso under MIT. Their original license notices are included under `language-tools` in the installed and portable distributions. The release build locks the npm dependency and verifies the Python wheel hashes.

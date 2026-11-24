# Mason Agent brand guide

<img src="characters/01-mortar.png" alt="Mortar, a violet paper-craft wombat with a steady half-smile" width="180" align="right">

Mason Agent is a Windows coding workspace that runs on your own local models. You add a project, decide what the agent may do and review every change before you keep it. The identity is about careful building, one checked step at a time. It has two parts, Mortar the mascot and the Mason Agent wordmark, and the tagline says what the product is for: **Make the next move clear.**

<picture>
  <source media="(prefers-color-scheme: dark)" srcset="logo-kit/png/logo-dark-1024w.png">
  <img src="logo-kit/png/logo-light-1024w.png" alt="Mason Agent logo: Mortar the wombat beside the Mason Agent wordmark" width="360">
</picture>

## The mascot

**Mortar** is a wombat cut from layers of violet and lilac card, with a broad cream face and a dark nose. The stepped cheeks and overlapping layers look built up piece by piece, like the work the app is for. Mortar is steady and quietly witty: someone who checks the work before saying it is done.

Four companions are made of the same paper. **Lintel** the heron is perceptive and brief, **Dovetail** the badger asks the exact question, **Rivet** the beetle is energetic and focused, and **Plumb** the seal is calm and dependable.

Two transparent pose masters are available for review and recovery.

| Pose | Suggested placement |
| --- | --- |
| [Mark this line](poses/mark-this-line.png) | Beside a draft diff comment while the reviewer marks a source line. |
| [On the mend](poses/on-the-mend.png) | Beside an active repair while Mason applies a correction or retries a failed edit. |

<img src="characters/01-mortar.png" alt="Mortar" width="88"> <img src="characters/02-lintel.png" alt="Lintel" width="88"> <img src="characters/03-dovetail.png" alt="Dovetail" width="88"> <img src="characters/04-rivet.png" alt="Rivet" width="88"> <img src="characters/05-plumb.png" alt="Plumb" width="88">

Where Mortar appears in the app:

- **Title bar and app icon.** Inside the app Mortar is the logo: a 32 px Mortar sits next to the wordmark, and the taskbar icon is Mortar too.
- **The agent's voice.** A small Mortar marks each of the agent's turns in a conversation, the way a person icon marks yours.
- **Welcome pages.** Mortar greets you on the first screen ("glad you're here") and waits on the empty project, session and chat pages.
- **Session status.** Next to a session, Mortar shows how it stands: "I'm on it" while working, "Let me check" when it waits for your review or approval, "Well played" when it finished with changes to review, "We're good" when it only answered, and "Oof" when it failed or was blocked.
- **Empty lists.** "Not convinced" appears when a search of your models finds nothing.

Mortar's eight moods are still images; nothing in the app animates in a loop. The companions are for profiles, documentation and community spaces, and do not appear in the app.

Please don't:

- Put Mortar inside the working surfaces, such as diffs, file views and command output.
- Use a cheerful mood for a failure, or any mascot art in place of the message. The text always says what happened.
- Recolour Mortar, add a badge or circle behind the cut-out, or crop the ears.
- Give the companions speaking parts in the app. Mortar is the only voice.

## Logo

The logo is Mortar beside the wordmark, the same pairing the app shows in its title bar.

| File (in `logo-kit/png`) | Use it for |
| --- | --- |
| `logo-light-512w`, `-1024w`, `-2048w` | The default: Mortar and the wordmark in ink, for light backgrounds. Transparent PNG. |
| `logo-dark-512w`, `-1024w`, `-2048w` | The same lockup with the wordmark in paper, for dark backgrounds. Transparent PNG. |
| `mascot-64px` to `mascot-1024px` | Mortar alone: favicons, profile pictures, app icons and other square spaces. |
| `brand-wordmark-outlined` (also in `logo-kit/svg`) | The lettering alone, for layouts that place Mortar themselves, as the app does. |

- **Minimum size.** In marketing and print, keep Mortar at least 32 px tall in the logo, which makes the logo about 215 px wide. Alone, Mortar can go down to 16 px, as in the favicon. The app's 48 px title bar uses a 32 px Mortar and a wordmark 16 px tall.
- **Clear space.** Leave a margin of half Mortar's height on every side.
- **Do** use the light logo on light backgrounds and the dark logo on dark ones, and use Mortar alone where the space is square.
- **Don't** stretch, rotate or recolour the logo, change the gap or the relative sizes of Mortar and the wordmark, add shadows or outlines, or set the name in a font yourself. The wordmark is drawn artwork (Manrope at weight 750, converted to outlines).

`logo-kit/preview/logo-usage.png` shows both logos and Mortar alone on one sheet.

## Colour

Warm paper, blue-black ink and a single violet. The dark theme is a separate set of colours, not the light one inverted. `tokens.json` and `tokens.css` hold the same values for code.

**Light theme**

| Swatch | Hex | Used for |
| --- | --- | --- |
| Paper | `#F8F6F2` | Window and page background |
| White | `#FFFFFF` | Cards, panels and message surfaces; text on violet buttons |
| Ink | `#20263C` | Text and the wordmark |
| Muted | `#535B72` | Secondary text and captions |
| Violet | `#4849A8` | Buttons, links, selected items and progress |
| Hairline | `#D9DBE1` | Borders of cards and panels |

**Dark theme**

| Swatch | Hex | Used for |
| --- | --- | --- |
| Night | `#171A2B` | Window and page background; text on lilac buttons |
| Raised | `#24283E` | Cards, panels and message surfaces |
| Paper | `#F8F6F2` | Text |
| Silver | `#C3C9E0` | Secondary text and captions |
| Bright | `#C3C2FF` | Buttons, links, selected items and progress, with Night text |
| Seam | `#3B4059` | Borders of cards and panels |

Contrast, measured with the WCAG formula: Ink on Paper is 13.9:1, Muted on Paper 6.3:1, Violet on Paper 7.0:1 and white on Violet 7.5:1. In dark, Paper on Night is 16.0:1, Silver on Night 10.5:1 and Night on Bright 10.2:1. When Windows high contrast is on, the app drops the brand colours and uses the system ones.

Corners are 8 px on cards and panels and 20 px on the pill-shaped starter prompts.

## Typography

- **Manrope** (SIL Open Font License 1.1) for the wordmark and for page and panel titles, which are set semibold. The font file and its licence are in `logo-kit/fonts`.
- **Segoe UI Variable**, the Windows system font, for controls, conversation text and body copy. It is part of Windows and is not shipped with the app.

## Voice

Direct, capable and calm. Mason Agent says what it will do, what it did and where the limits are.

| Write | Rather than |
| --- | --- |
| "Bring a project into focus." | "Welcome to the future of coding!" |
| "New sessions cannot edit files until you change the permission mode." | "Your code is always safe with us." |
| "This project has no sessions yet. Its files are untouched; start a conversation when you're ready." | "Nothing here yet!" |
| "Review a session's file changes as diffs and restore originals." | "Undo AI mistakes with one click." |
| "Look through this project and suggest one small, low-risk improvement. Explain what you would change before editing anything." | "Let the agent refactor everything for you." |
| "Could not start a session", followed by the reason | "Oops, something went wrong." |

Name the file, the command and the result. Say "on this computer" when something stays local, and never promise more than the model and the permission mode allow.

## Files in this folder

| Path | What it holds |
| --- | --- |
| `tokens.json`, `tokens.css` | The palette, light and dark theme colours, fonts and corner radii, for code |
| `logo-kit/png` | The logos (512 to 2048 px wide), Mortar alone (64 to 1024 px) and the wordmark (512 to 4096 px wide) |
| `logo-kit/svg` | The wordmark as vector outlines |
| `logo-kit/favicon` | Favicons and touch icons made from Mortar, and a web manifest |
| `logo-kit/preview` | The logo usage sheet |
| `logo-kit/fonts` | Manrope and its licence |
| `characters` | Mortar and the four companions on round backdrops, 512 px |
| `graphics` | Share images as PNG: 1200 × 630 Open Graph, 1080 × 1080 square, 1500 × 500 header, a 1024 px profile picture and a 512 px maskable icon |

The app keeps its own copies of the art it shows, including Mortar's mascot art, in [`src/MasonAgent/Assets`](../src/MasonAgent/Assets). The website is in [`site`](../site).

## Licence

Everything in this repository, code and artwork alike, is under the [MIT licence](../LICENSE). Manrope is under the [SIL Open Font License 1.1](logo-kit/fonts/LICENSE-Manrope.txt).

# Code workspace design QA

Mason Agent 2.1.0 implements the three approved concepts as views of one coding session. Each view uses the existing native controls, session journal and permissions.

| View | Compared with the approved composition | Interaction exercised |
| --- | --- | --- |
| Workbench | Projects at left, conversation and composer in the center, live WebView2 preview at right. Mortar marks the completed block with a check count, observed totals and an expandable receipt. | Opened the existing local HTML preview inside the app. Expanded the discount receipt and observed two clicks for $20 and three for $27. |
| Review | Unified diff with source line numbers and a right-hand check inspector. Pencil Mortar appears at the line being discussed. | Added comments to updated lines 2 and 1. Restarted with both pending, then sent the two comments and the shared draft to Qwen2.5-Coder-7B. The journal recorded each anchor once. |
| Notebook | Paper-toned session, wider reading column, shared composer and an inline verification section. | Switched from Workbench, typed a draft, opened Review, then returned to Notebook. Closing and reopening Mason restored the selected view, text and both pending anchors. |

The visual pass compared native captures at 1707 × 1020 against all three reference images side by side. After the first comparison the Workbench result heading and mascot were enlarged, Notebook's transcript and composer were aligned, and Review's evidence rows were made independently expandable. The compact pass used a 1080 × 940 physical window at 150% scale. The view picker opened Review and the pane selector exposed both the diff and the inspector. After a later Ask turn the inspector correctly displayed an empty checks state for that turn.

## Design deviations

- Notebook applies its paper theme to the session surface. The native title bar and window status strip retain the user's appearance settings.
- Projects, line contents, receipts and model labels come from the open session. The captured diff shows an entirely new HTML file, so most lines are additions. The WinUI flyout positions itself against the selected line and available space.
- All three views keep explicit navigation and native keyboard-accessible controls. Workbench retains the actual request, intermediate messages and preview controls, so content density varies with the conversation and window size.

## Acceptance

Final visual and interaction result: passed for the three views and the compact review flow. The live review check verified prompt delivery, source anchors and response receipt. Qwen correctly described standards mode but misidentified the language attribute in its answer. The eight browser receipts displayed during this UI check belonged to an existing verified run.

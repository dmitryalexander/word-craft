# WordCraft 2.3.2

Ctrl+Tab now returns to the beginning of the text in paused reading from writing or reading. Plain Tab still switches writing/reading. Ctrl+mouse wheel changes the actual native font size on the editable cloth mesh (Ctrl+A), with smooth fractional wheel steps and bounds of 8–160 DIPs. Source text, selection and undo are preserved; cached textile fringes refresh after reflow. Reading/smoke text sizes stay unchanged. Inset simulation presentation now has 20-DIP rounded corners over its black surround; at 100% it fills the window edge to edge. Plain wheel remains simulation-only zoom from 25–100%, and help/options remain full size. The centered, left-aligned F1 keycap guide toggles on each press. Dense cloth fibers are batched into at most128 paths to reduce text-material rebuild stalls. Ctrl+Shift+Space replay and Shift+Space typing remain.

- Native smoke writing and kinetic text reading, with smooth mode/color transitions.
- Editable woven text on a cloth simulation with fixed corner anchors, fraying and live material/motion controls.
- Morse-inspired typing/reading audio and independent dark noise layers.
- Independent color, audio and motion preferences; replay, scrubbing and fullscreen controls.
- Self-contained source snapshot and Windows build/test/package automation.

Extract the whole portable ZIP and open `Open WordCraft.cmd`. Requires Windows 10/11 x64, .NET Framework 4.8 and Direct3D 11 / Shader Model 5 graphics. The build is unsigned.

Drafts are session-only: copy your text elsewhere before closing. No document Save/Open or draft recovery is provided. Desktop motion/listening and performance on a second computer still need manual review. See README for controls and limitations.

# README assets

Launch and documentation media live here. The root `README.md` references these paths.

## Required before launch

| File             | Purpose                     | Spec                                                                 |
| ---------------- | --------------------------- | -------------------------------------------------------------------- |
| `demo.gif`       | Top-of-README hero demo     | 10-20s, target < 10 MB, 720p+, shows raw dictation streaming live    |
| `clean-mode.gif` | Clean-mode rewrite demo     | 5-10s, optional, shows `Ctrl+Alt+H` rewriting on release             |
| `overlay.png`    | Listening overlay still     | The floating pill shown while dictating                              |
| `settings.png`   | Settings window still       | Used by `docs/usage.md`                                              |

## Capture tips

- Record at the resolution you want displayed — GIFs do not scale down cleanly.
- Keep `demo.gif` under ~10 MB so it loads inline on GitHub. For higher quality, attach an MP4 to a GitHub Release and link it instead.
- Use a clean desktop, neutral wallpaper, and a real target app (editor, browser, chat) so the "types into any field" story is obvious.
- Show the hotkey on screen (an on-screen key display) so viewers understand the push-to-talk interaction.
- The moment that sells it is text appearing word-by-word as you speak. Make that the focal point of the hero shot.

Once `demo.gif` is added, uncomment the image line in the root `README.md` Demo section and remove the "pending" note.

# Google Stitch Design Prompt for NovaDownload

Copy/paste this into https://stitch.withgoogle.com/ if MCP/integration unavailable.

```
Design a premium Windows 11 WPF download manager called "NovaDownload".

Goal: Clean, modern, professional. Inspired by IDM but independently designed.

Screens:
1. Dashboard - stats cards (Total, Active, Completed, Failed), current speed, recent downloads, quick actions
2. Downloads - list with file, size, progress, speed, ETA, status, actions (pause/resume/cancel/open folder)
3. Add Download - URL, filename, destination, options, start/queue
4. Details - transfer graph/progress, connections, retries, errors
5. Queue - concurrency, priorities, start/stop/pause all
6. History - completed/failed, search/filter, retry, open folder
7. Settings - general, network, browser, notifications, theme (light/dark), logging

Style:
- Dark first with light theme
- Rounded corners 8-12px
- Subtle shadows, soft borders
- Accent blue #4F9BFF
- Status colors
- Clean typography
- Minimal icons
- Smooth hover/focus states

WPF constraints:
- Do NOT output raw HTML/React/Tailwind as final WPF XAML blindly
- Extract layout, spacing, colors, typography, components
- Provide XAML-friendly specs: Grid/StackPanel, styles, templates, resources
- Include color tokens, spacing scale, component library
- Export assets as SVG/PNG if possible
- Provide wireframes + high-fidelity + design tokens JSON
```

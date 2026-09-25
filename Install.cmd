@echo off
rem Installs Markdown Viewer for the current Windows user. No administrator rights needed.
rem It copies this folder to %LOCALAPPDATA%\Programs\MarkdownViewer, adds a Start menu entry,
rem and adds "Markdown Viewer" to "Open with" for .md files. Remove it from Settings > Apps.
"%~dp0MarkdownViewer.exe" --install

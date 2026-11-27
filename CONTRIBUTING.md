# Contributing to Traydio

Thank you for your interest in Contributing to Traydio! This document provides guidelines for contributing to the project.

## How to Contribute

### Reporting Bugs

If you find a bug, please create an issue on GitHub with:
- A clear, descriptive title
- Steps to reproduce the issue
- Expected vs actual behavior
- Your environment (Windows version, app version)
- Screenshots if applicable

### Suggesting Features

Feature requests are welcome! Please create an issue with:
- A clear description of the feature
- Why it would be useful
- Any implementation ideas you might have

### Pull Requests

1. **Fork the repository** and create your branch from `main`
2. **Make your changes** following the coding standards below
3. **Test your changes** thoroughly
4. **Update documentation** if needed
5. **Commit your changes** with clear, descriptive messages
6. **Create a pull request** with a clear description of the changes

## Coding Standards

- Follow C# coding conventions
- Use meaningful variable and method names
- Add XML documentation comments for public APIs
- Keep methods focused and single-purpose
- Use nullable reference types appropriately
- Remove debug logging statements before committing

## Development Setup

See the [README.md](README.md) for build instructions.

### Prerequisites
- Visual Studio 2026 with .NET 11 support, or VS Code
- .NET 11 SDK
- Windows App SDK

### Add a new translation
- Copy the latest Strings\en-US folder and rename appropriately 
- Translate the new Resources.resw
- Add the new SettingsPage_Language_name.Content property in all the other Resources.resw files
- Update the Services\LocalizationService.SupportedLanguages static list
- Update the ComboBox list in Pages\SettingsPage.xaml

## Code of Conduct

This project follows a simple code of conduct:
- Be respectful and inclusive
- Provide constructive feedback
- Focus on what is best for the community

## Questions?

Feel free to open an issue with the "question" label or reach out to [@TheJoeFin](https://github.com/TheJoeFin).

## License

By contributing, you agree that your contributions will be licensed under the MIT License.

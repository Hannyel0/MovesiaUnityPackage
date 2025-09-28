# Movesia Unity Package

![Movesia Logo](Editor/EditorResources/icons/movesia-connected-dark.png)

## Overview

**Movesia** is an innovative Unity Editor package that brings agentic AI-powered game development assistance directly into your Unity workflow. This package establishes a real-time connection between Unity Editor and an external AI agent, enabling intelligent automation, code generation, and development assistance.

## What is Movesia?

Movesia is an **agentic game development agent** that acts as your intelligent development companion. It monitors your Unity project in real-time, understands your scene hierarchy, tracks changes, and provides contextual assistance to accelerate your game development process.

## Key Features

### 🔗 **Real-time Unity Integration**
- **WebSocket Connection**: Establishes a persistent connection to the Movesia AI agent via WebSocket (default: `ws://127.0.0.1:8765`)
- **Session Management**: Maintains persistent sessions across Unity domain reloads
- **Connection Status Indicator**: Visual toolbar indicator showing connection state (Connected/Connecting/Disconnected)

### 🏗️ **Intelligent Hierarchy Tracking**
- **Scene Monitoring**: Automatically tracks changes in your Unity scene hierarchy
- **Delta Detection**: Efficiently detects and reports only the changes that matter
- **Multi-Scene Support**: Handles complex projects with multiple scenes
- **Object Change Events**: Captures granular object modifications, additions, and deletions

### 🎯 **Smart Event System**
- **Hierarchy Events**: Real-time notifications of scene structure changes
- **Object Events**: Detailed tracking of GameObject modifications
- **Scene Events**: Monitoring of scene loading, saving, and switching operations
- **Manifest Sharing**: Automatic project structure communication with the AI agent

### 🛠️ **Developer Experience**
- **Toolbar Integration**: Clean, unobtrusive UI elements in the Unity toolbar
- **Visual Feedback**: Clear connection status with themed icons
- **Automatic Reconnection**: Robust connection handling with automatic retry logic
- **Session Persistence**: Maintains context across Unity restarts

## Installation

### Prerequisites
- Unity 2021.3 or later
- Unity Toolbar Extender UI Toolkit package
- Newtonsoft.Json package
- NativeWebSocket package

### Setup Steps

1. **Import the Package**: Add the Movesia package to your Unity project
2. **Configure Connection**: Update the connection token in `MovesiaConnection.cs`:
   ```csharp
   private const string Token = "YOUR_MOVESIA_TOKEN";
   ```
3. **Start Movesia Agent**: Ensure the Movesia AI agent is running on your local machine
4. **Connect**: The package will automatically attempt to connect when Unity starts

## Architecture

### Core Components

#### `MovesiaConnection.cs`
The heart of the package, responsible for:
- WebSocket connection management
- Message serialization and communication
- Session handling and persistence
- Automatic reconnection logic
- Heartbeat mechanism for connection health

#### `MovesiaHierarchyTracker.cs`
Intelligent scene monitoring system that:
- Tracks GameObject hierarchy changes
- Detects object modifications, additions, and deletions
- Manages scene state snapshots
- Provides efficient delta reporting
- Handles multi-scene scenarios

#### `MovesiaToolbar.cs`
User interface components including:
- Connection status indicator
- Visual feedback system
- Toolbar integration
- State management and persistence

#### `MovesiaEvents.cs`
Event system for:
- Hierarchy change notifications
- Object modification events
- Scene lifecycle events
- Custom event handling

## Usage

Once installed and configured, Movesia works automatically in the background:

1. **Automatic Connection**: The package connects to the Movesia agent when Unity starts
2. **Real-time Monitoring**: Your scene hierarchy and changes are continuously tracked
3. **AI Assistance**: The connected AI agent can provide contextual help and automation
4. **Visual Feedback**: Check the toolbar for connection status

### Connection States

- **🟢 Connected**: Successfully connected to Movesia agent
- **🟡 Connecting**: Attempting to establish connection
- **🔴 Disconnected**: No connection to Movesia agent

## Configuration

### Connection Settings
- **Default Port**: 8765
- **Default Host**: 127.0.0.1 (localhost)
- **Token**: Configure in `MovesiaConnection.cs`

### Customization
The package supports various customization options:
- Connection parameters
- Event filtering
- UI theming
- Logging levels

## Technical Details

### Dependencies
- **Unity 2021.3+**: Core Unity functionality
- **Newtonsoft.Json**: JSON serialization for communication
- **NativeWebSocket**: WebSocket client implementation
- **Unity Toolbar Extender**: UI toolkit integration

### Communication Protocol
- **WebSocket-based**: Real-time bidirectional communication
- **JSON Messages**: Structured data exchange
- **Event-driven**: Reactive architecture for efficient updates
- **Session-aware**: Persistent context across connections

### Performance Considerations
- **Efficient Delta Tracking**: Only reports actual changes
- **Asynchronous Operations**: Non-blocking Unity Editor integration
- **Memory Optimization**: Smart caching and cleanup
- **Minimal Overhead**: Designed for seamless development experience

## Troubleshooting

### Common Issues

**Connection Failed**
- Ensure Movesia agent is running
- Check firewall settings
- Verify token configuration

**Hierarchy Not Updating**
- Check Unity console for errors
- Verify scene is properly loaded
- Restart Unity if needed

**Performance Issues**
- Monitor large scene hierarchies
- Check for excessive object changes
- Review event filtering settings

## Support

For support, issues, or feature requests:
- **Email**: support@movesia.com
- **Documentation**: [Coming Soon]
- **Community**: [Coming Soon]

## Version History

### v0.1.0 (Current)
- Initial release
- Core WebSocket connectivity
- Hierarchy tracking system
- Toolbar integration
- Session management

## License

© 2024 Movesia. All rights reserved.

---

**Movesia** - Empowering game developers with intelligent AI assistance, one Unity project at a time.
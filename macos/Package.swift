// swift-tools-version: 5.9
import PackageDescription

let package = Package(
    name: "FluentScannerMac",
    platforms: [.macOS(.v14)],
    targets: [
        .executableTarget(
            name: "FluentScanner",
            path: "Sources/FluentScanner",
            exclude: ["Resources"]
        ),
    ]
)

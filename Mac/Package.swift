// swift-tools-version:5.9
import PackageDescription

let package = Package(
    name: "VPNAutoConnect",
    platforms: [.macOS(.v13)],
    targets: [
        .executableTarget(
            name: "VPNAutoConnect",
            path: "Sources/VPNAutoConnect"
        ),
        .testTarget(
            name: "VPNAutoConnectTests",
            dependencies: ["VPNAutoConnect"],
            path: "Tests/VPNAutoConnectTests"
        ),
    ]
)

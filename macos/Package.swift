// swift-tools-version:5.10
// Mason Agent for macOS: a Swift engine (MasonCore), the mason command line tool and, on macOS, the SwiftUI app.
// MasonCore and mason use Foundation only, so they also build and run their tests on Linux.
import PackageDescription

let package = Package(
    name: "MasonAgent",
    platforms: [.macOS(.v14)],
    products: [
        .library(name: "MasonCore", targets: ["MasonCore"]),
        .executable(name: "mason", targets: ["mason"]),
    ],
    targets: [
        .target(name: "MasonCore"),
        .executableTarget(name: "mason", dependencies: ["MasonCore"]),
        .testTarget(name: "MasonCoreTests", dependencies: ["MasonCore"]),
    ]
)

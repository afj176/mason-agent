// swift-tools-version:5.10
// Mason Agent for macOS: a Swift engine (MasonCore), the mason command line tool and the SwiftUI app.
// MasonCore and mason use Foundation only, so they also build and run their tests on Linux.
// The app target exists only on macOS because it needs SwiftUI and AppKit.
import PackageDescription

var products: [Product] = [
    .library(name: "MasonCore", targets: ["MasonCore"]),
    .executable(name: "mason", targets: ["mason"]),
]
var targets: [Target] = [
    .target(name: "MasonCore"),
    .executableTarget(name: "mason", dependencies: ["MasonCore"]),
    .testTarget(name: "MasonCoreTests", dependencies: ["MasonCore"]),
]
#if os(macOS)
products.append(.executable(name: "MasonAgent", targets: ["MasonApp"]))
targets.append(.executableTarget(name: "MasonApp", dependencies: ["MasonCore"]))
#endif

let package = Package(name: "MasonAgent", platforms: [.macOS(.v14)], products: products, targets: targets)

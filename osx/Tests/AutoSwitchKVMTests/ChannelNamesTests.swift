import XCTest

@testable import AutoSwitchKVM

/// Channel names (HID++ 0x1815 "Hosts Info", spec §5a): decoding and the channel choice labels.
final class ChannelNamesTests: XCTestCase {
    func testLabelsUseTheStoredNameWhenThereIsOne() {
        let names: [String?] = ["ENGIN-PC", nil, "Engin’s MacBook Pro"]
        XCTAssertEqual(HostChannels.label(0, names: names), "Channel 1 · ENGIN-PC")
        XCTAssertEqual(HostChannels.label(1, names: names), "Channel 2")
        XCTAssertEqual(HostChannels.label(2, names: names), "Channel 3 · Engin’s MacBook Pro")
        XCTAssertEqual(HostChannels.label(3, names: names), "Channel 4")  // more channels than names
        XCTAssertEqual(HostChannels.label(1, names: nil), "Channel 2")
    }

    func testDecodesNamesAcrossPiecesAndDropsPadding() {
        // "Engin’s MacBook Pro" is 21 UTF-8 bytes: two 14-byte pieces, the second NUL-padded.
        let utf8 = Array("Engin’s MacBook Pro".utf8)
        let pieces = Array(utf8.prefix(14)) + Array(utf8.dropFirst(14)) + [UInt8](repeating: 0, count: 7)
        XCTAssertEqual(HostChannels.decodeName(pieces, length: utf8.count), "Engin’s MacBook Pro")
        XCTAssertEqual(HostChannels.decodeName(Array("PC\0\0".utf8), length: 4), "PC")
        XCTAssertNil(HostChannels.decodeName([0, 0, 0], length: 3))
        XCTAssertNil(HostChannels.decodeName([], length: 0))
    }
}

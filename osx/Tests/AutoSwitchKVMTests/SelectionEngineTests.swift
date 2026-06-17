import XCTest
@testable import AutoSwitchKVM

@MainActor
final class SelectionEngineTests: XCTestCase {

    private let vendor: UInt16 = 0x05E3
    private let product: UInt16 = 0x0626
    private let addr = "3c-50-02-bf-22-45"

    private func makeStore() -> ConfigStore {
        let tmp = FileManager.default.temporaryDirectory.appendingPathComponent(UUID().uuidString)
        return ConfigStore(directory: tmp)
    }

    private func makeEngine(managePairing: Bool = false,
                            enabled: Bool = true) -> (SelectionEngine, MockBluetoothController, ConfigStore) {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [BTDevice(name: "Trackpad", address: addr,
                                         enabled: enabled, managePairing: managePairing)]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)
        return (engine, bt, store)
    }

    func testConnectsWhenSourceArrives() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testDisconnectsWhenSourceLeaves() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertFalse(engine.selected)
        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    func testPairsBeforeConnectWhenManagePairing() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        guard let pairIndex = bt.calls.firstIndex(of: "pair:\(addr)"),
              let connectIndex = bt.calls.firstIndex(of: "connect:\(addr)") else {
            return XCTFail("expected both pair and connect calls; got \(bt.calls)")
        }
        XCTAssertLessThan(pairIndex, connectIndex, "pair must happen before connect")
    }

    func testUnpairsOnLeaveWhenManagePairing() async {
        let (engine, bt, _) = makeEngine(managePairing: true)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertTrue(bt.calls.contains("unpair:\(addr)"))
    }

    func testDoesNotUnpairOnLeaveWhenNotManagingPairing() async {
        let (engine, bt, _) = makeEngine(managePairing: false)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        engine.handleUSB((vendorID: vendor, productID: product, added: false))
        await engine.evaluateNow()

        XCTAssertFalse(bt.calls.contains("unpair:\(addr)"))
    }

    func testIgnoresUnrelatedUSBDevice() async {
        let (engine, bt, _) = makeEngine()
        engine.handleUSB((vendorID: 0x1234, productID: 0x5678, added: true))
        await engine.evaluateNow()

        XCTAssertFalse(engine.selected)
        XCTAssertTrue(bt.connectedAddrs.isEmpty)
    }

    func testDisabledDeviceIsNotConnected() async {
        let (engine, bt, _) = makeEngine(enabled: false)
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)               // source is present
        XCTAssertTrue(bt.connectedAddrs.isEmpty)     // but disabled device is left alone
    }

    func testMultiDeviceSourceStaysSelectedUntilAllGone() async {
        let store = makeStore()
        let p1: UInt16 = 0x0626
        let p2: UInt16 = 0x0610
        store.config.source = USBSource(name: "KVM", vendorID: vendor, productIDs: [p1, p2])
        store.config.devices = [BTDevice(name: "Trackpad", address: addr, enabled: true)]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        engine.handleUSB((vendorID: vendor, productID: p1, added: true))
        engine.handleUSB((vendorID: vendor, productID: p2, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        // One member leaves — still selected because the other remains.
        engine.handleUSB((vendorID: vendor, productID: p1, added: false))
        await engine.evaluateNow()
        XCTAssertTrue(engine.selected)

        // Last member leaves — now deselected.
        engine.handleUSB((vendorID: vendor, productID: p2, added: false))
        await engine.evaluateNow()
        XCTAssertFalse(engine.selected)
    }

    func testPausedSkipsAutomaticConnect() async {
        let (engine, bt, store) = makeEngine()
        store.config.paused = true

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected, "selection should reflect real presence even while paused")
        XCTAssertTrue(bt.connectedAddrs.isEmpty, "paused automation should not connect")
    }

    func testResumingFromPauseReconcilesState() async {
        let (engine, bt, store) = makeEngine()
        store.config.paused = true

        // Source present while paused: presence reflected, but nothing connected.
        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.isEmpty)

        // Resume → should connect to match the present source.
        store.config.paused = false
        await engine.evaluateNow()
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testConnectAllNowWorksRegardlessOfSelection() async {
        let (engine, bt, _) = makeEngine()
        // Source is NOT present, so automation wouldn't connect.
        await engine.connectAllNowImpl()
        XCTAssertTrue(bt.connectedAddrs.contains(addr))
    }

    func testDisconnectAllNowDisconnects() async {
        let (engine, bt, _) = makeEngine()
        try? await bt.connect(addr)
        await engine.disconnectAllNowImpl()
        XCTAssertFalse(bt.connectedAddrs.contains(addr))
    }

    func testBluetoothOffSkipsConnect() async {
        let (engine, bt, _) = makeEngine()
        bt.powered = false
        await engine.refreshPower()
        XCTAssertFalse(engine.bluetoothPowered)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertTrue(engine.selected)                 // presence still tracked
        XCTAssertTrue(bt.connectedAddrs.isEmpty)       // but no connect while BT is off
        XCTAssertEqual(engine.statuses.values.first, .bluetoothOff)
    }

    func testBackoffIsExponentialAndCapped() {
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 1), 2)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 2), 4)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 3), 8)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 4), 16)
        XCTAssertEqual(SelectionEngine.backoffSeconds(base: 2, attempt: 6), 30)   // capped
    }

    func testConnectsInDeviceListOrder() async {
        let store = makeStore()
        store.config.source = USBSource(name: "Hub", vendorID: vendor, productIDs: [product])
        store.config.devices = [
            BTDevice(name: "First", address: "aa-aa", enabled: true),
            BTDevice(name: "Second", address: "bb-bb", enabled: true)
        ]
        let usb = MockUSBMonitor()
        let bt = MockBluetoothController()
        let engine = SelectionEngine(store: store, usb: usb, bt: bt)

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls, ["connect:aa-aa", "connect:bb-bb"])
    }

    func testAlreadyConnectedSkipsRedundantConnect() async {
        let (engine, bt, _) = makeEngine()
        try? await bt.connect(addr)                  // pretend it's already connected
        let callsBefore = bt.calls.count

        engine.handleUSB((vendorID: vendor, productID: product, added: true))
        await engine.evaluateNow()

        XCTAssertEqual(bt.calls.count, callsBefore, "should not re-connect an already-connected device")
    }
}

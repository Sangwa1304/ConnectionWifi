# Android Wi-Fi Direct

`AndroidWifiDirectService` implements the shared `WindowsWifiLibrary.Interfaces.IWifiDirectService` contract from `src/WifiDirect.Contracts`. The Android app's ViewModel consumes that interface; it does not use Android-only peer/status DTOs.

## Connection orchestration

- The shared peer identity, status enums, publisher/watcher status events, invitation metadata and connection event args are platform-neutral.
- The initiator role is represented by `WifiDirectConnectionEventArgs.ServerMode`, using the same Windows convention.
- Discovery does not establish a connection. An outgoing connection completes only after Android reports a formed P2P group.
- Android system UI controls incoming Wi-Fi Direct consent. The app never auto-accepts or silently connects. The Android public API cannot replace the system's incoming P2P confirmation with an app-owned invitation dialog.
- The app separately asks the user to accept or refuse every incoming file-transfer batch.

## Permissions and system controls

`InitializeAsync` requests `NEARBY_WIFI_DEVICES` on Android 13+ and location permission on earlier releases. The app opens the native connectivity panel instead of attempting to toggle Wi-Fi programmatically.

## Windows TCP compatibility

Android reuses `TcpServerStreaming`, `TcpClientStreaming`, `InfoMeta` and the existing TCP framing from `ServiceTcpChared`. It preserves the same two ports and role-dependent server/client ordering as the Windows ViewModel.

When Android is the group owner, `WifiP2pInfo` does not provide the remote client's IP. The Android transport starts the role-correct TCP server and reads the actual remote endpoint from the accepted Windows TCP socket; it does not guess the DHCP address.

Received files are SHA-256 validated by the shared transport and stored in the app's private `Received` directory. The Android UI exposes native Open and Share actions for those files.

## Android platform behavior

The Windows AdvertisementPublisher + DeviceWatcher pair cannot be reproduced as a simultaneous pair with the public Android P2P API. Android group publication and peer discovery are separate operations. The UI exposes them separately, while the shared interface remains consistent.

## Validation

The repository changes have not yet been compiled or tested on physical Windows/Android devices. Verify both initiator directions, both group-owner roles, accept/refuse flows, large and multiple files, cancellation, reconnect, hash validation and Android API/OEM variations before release.

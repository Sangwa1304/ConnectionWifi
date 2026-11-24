# Android Wi-Fi Direct

The Android implementation is exposed by `AndroidWifiDirectService` and registered as a singleton in MAUI dependency injection.

## Connection consent

- Discovering a peer never connects to it.
- `ConnectToPeerAsync` only submits a negotiation request. It does not report success from the `ActionListener.OnSuccess` callback.
- The service completes the request only after Android reports `GroupFormed == true`.
- On Android 13+, `WIFI_P2P_REQUEST_RESPONSE_CHANGED_ACTION` is also observed so an explicit rejection is reported immediately.
- Incoming connection consent is controlled by Android's Wi-Fi Direct system UI. The public `WifiP2pManager` API does not expose a normal app callback to intercept an incoming request and replace that system approval with an in-app Accept/Reject dialog. The app therefore does not auto-accept incoming peers.

## Permissions

`InitializeAsync` requests `NEARBY_WIFI_DEVICES` on Android 13+ and `ACCESS_FINE_LOCATION` on earlier Android versions. The manifest declares the Wi-Fi state/change permissions required by `WifiP2pManager`.

## TCP integration limitation

Android's `WifiP2pInfo` provides the group-owner address, not a guaranteed remote-client address. When Android is group owner, Android may redact that address for ordinary applications. Do not pass `GroupOwnerAddress` as though it were always the Windows peer's IP. The TCP layer needs a role-aware endpoint strategy (or a small authenticated endpoint handshake after P2P formation) before file transfer can be considered interoperable in both directions.

## Lifecycle

Call `InitializeAsync` while the app is in the foreground, then call `StartSubscribingAsync` for discovery or `StartPublishingAsync` to create a group owner. Call `DisconnectAsync` to remove the current group and `Dispose` when the service is no longer needed.

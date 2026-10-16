# Security and privacy

Please report security or privacy concerns through a private GitHub security advisory rather than a public issue.

Qpro Companion processes inward-facing eye and mouth cameras and requires root on the headset. Recordings and generated profiles can contain biometric data. They are ignored by the supplied `.gitignore`, but check commits and archives before publishing.

Wireless ADB exposes the headset's debugging service on the local network. Use it only on a trusted private network and disable it when you no longer need it. The camera preview service binds to localhost; Qpro does not expose a direct LAN camera server.

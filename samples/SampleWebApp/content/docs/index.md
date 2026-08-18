# Docs, inside the host layout

This mount sets `LayoutPath`, so the component renders into the sample application's layout —
the dark header above this content belongs to the host, everything below it to the package.

The component declares no Razor sections, which is what makes this safe: a layout is under no
obligation to render one, and an unrendered declared section throws at request time.

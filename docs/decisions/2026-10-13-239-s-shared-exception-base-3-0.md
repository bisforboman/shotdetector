# #239's shared exception base: 3.0 (2026-10-13)

The user asked why a base class would be breaking. ShotDetectionException : FrameReaderException would be (it loses
InvalidOperationException), but a new MediaException : InvalidOperationException in FrameReader under both isn't
(an inserted base; FrameReaderException only gains ancestors), its one behaviour change being that catch
(InvalidOperationException) also catches FrameReader's failures. Asked whether to do that in 2.x: **keep it for
3.0**, over the base now or with a shared Reason too.

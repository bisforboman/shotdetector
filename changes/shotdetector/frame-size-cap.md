### Fixed

- A video whose frames are larger than 8192 x 8192 pixels fails to decode (`InvalidInput`/`DecodeFailed`), with the
  executable and in-process, so a crafted file can't exhaust memory. No real video comes near it.

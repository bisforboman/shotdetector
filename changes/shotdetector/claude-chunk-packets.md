### Faster

- Parallel detection probes the file once: each chunk's reader takes the first reader's stream properties and packet
  timestamps instead of reading them again. Most noticeable on MPEG-TS, where reading every packet's timestamp is slow:
  a 4-minute, 280 MB clip went from 6.7 s to 5.8 s in 2 chunks on 4 CPUs (and from 21 s to 17 s of CPU time in 4).

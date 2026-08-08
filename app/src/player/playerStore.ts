import { create } from 'zustand';

export interface Track {
  storyId: string;
  variant: string;
  title: string;
  url: string;
}

interface PlayerState {
  queue: Track[];
  index: number;
  playing: boolean;
  play(tracks: Track[], startIndex: number): void;
  toggle(): void;
  next(): void;
  prev(): void;
}

/**
 * Global playback queue. The single <audio> element lives in PlayerBar (shell level)
 * and follows this state; routes only dispatch into the store.
 */
export const usePlayerStore = create<PlayerState>()((set, get) => ({
  queue: [],
  index: 0,
  playing: false,

  play: (tracks, startIndex) => {
    if (tracks.length === 0) {
      set({ queue: [], index: 0, playing: false });
      return;
    }
    const index = Math.min(Math.max(startIndex, 0), tracks.length - 1);
    set({ queue: tracks, index, playing: true });
  },

  toggle: () => {
    const { queue, playing } = get();
    if (queue.length === 0) return;
    set({ playing: !playing });
  },

  next: () => {
    const { queue, index } = get();
    if (queue.length === 0) return;
    if (index + 1 < queue.length) {
      set({ index: index + 1, playing: true });
    } else {
      // End of queue: stop, keep position so play resumes the last track.
      set({ playing: false });
    }
  },

  prev: () => {
    const { queue, index } = get();
    if (queue.length === 0) return;
    set({ index: Math.max(0, index - 1), playing: true });
  },
}));

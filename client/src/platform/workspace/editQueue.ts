import { ApiError } from '../api';

/**
 * Sends one user's edits one at a time, each against the revision the previous one produced. Edits typed
 * faster than the server answers therefore chain on this session's own changes; a revision moved by anyone
 * else is still refused by the server (ADR-0003, spine 6), and every edit queued behind the refusal is
 * dropped with it rather than sent against a workspace the user has not seen.
 */
export class EditQueue<Edit, View extends { readonly revision: number }> {
  #revision: number;
  #tail: Promise<void> = Promise.resolve();
  #broken = false;
  #pending = 0;

  constructor(
    revision: number,
    private readonly send: (revision: number, edit: Edit) => Promise<View>,
    private readonly applied: (view: View) => void,
    private readonly refused: (edit: Edit, error: unknown) => void,
  ) {
    this.#revision = revision;
  }

  get revision(): number {
    return this.#revision;
  }

  /** The workspace was read again (after a refusal or another command): later edits chain on this revision. */
  rebase(revision: number): void {
    this.#revision = revision;
    this.#broken = false;
  }

  /**
   * The page now shows the workspace as read at this revision, moved by someone else (the AI, another page). With
   * nothing of this session's waiting, later edits are taken against what is on screen, so they chain on it; while
   * an edit is waiting, its own answer names the revision, and a refusal reads the workspace again.
   */
  shown(revision: number): void {
    if (this.#pending === 0 && revision > this.#revision) this.rebase(revision);
  }

  push(edit: Edit): Promise<void> {
    this.#pending++;
    this.#tail = this.#tail.then(async () => {
      if (this.#broken) {
        this.refused(edit, new Error('An earlier edit was refused; this one was not sent.'));
        this.#pending--;
        return;
      }
      try {
        const view = await this.send(this.#revision, edit);
        this.#revision = view.revision;
        this.applied(view);
      } catch (error) {
        if (error instanceof ApiError && error.envelope.code === 'RevisionMoved') this.#broken = true;
        this.refused(edit, error);
      } finally {
        this.#pending--;
      }
    });
    return this.#tail;
  }
}

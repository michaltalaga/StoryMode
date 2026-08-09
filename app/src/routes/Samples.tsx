/* TYMCZASOWE: półka odsłuchowa próbek TTS (library/samples) do porównań silników
   i głosów. Usunąć razem z trasami /api/samples po zakończeniu kwestii polskiego TTS. */
import { useQuery } from '@tanstack/react-query';
import { getJson } from '../api/client';
import { useStrings } from '../i18n';

interface SampleDto {
  file: string;
  sizeKb: number;
}

export default function Samples() {
  const strings = useStrings();
  const query = useQuery({
    queryKey: ['samples'],
    queryFn: () => getJson<SampleDto[]>('/api/samples'),
  });

  return (
    <div className="space-y-4">
      <h1 className="font-serif text-2xl text-stone-900">{strings.samplesTitle}</h1>
      <p className="text-sm text-stone-500">{strings.samplesHint}</p>
      {query.isLoading && <p className="text-stone-500">{strings.loading}</p>}
      {query.isError && <p className="text-rose-700">{strings.samplesLoadError}</p>}
      <ul className="space-y-3">
        {query.data?.map((s) => (
          <li key={s.file} className="rounded-xl border border-stone-200 bg-white p-4">
            <div className="mb-2 flex items-baseline justify-between gap-2">
              <span className="break-all text-sm font-medium text-stone-800">{s.file}</span>
              <span className="shrink-0 text-xs text-stone-400">{Math.round(s.sizeKb)} KB</span>
            </div>
            <audio controls preload="none" className="w-full" src={`/api/samples/${s.file}`} />
          </li>
        ))}
      </ul>
    </div>
  );
}

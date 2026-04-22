import { useTranslation } from 'react-i18next';
import { Search, Type, Layers, XCircle, Palette, Monitor, Globe, Download, BookOpen } from 'lucide-react';

const GithubIcon = ({ className }: { className?: string }) => (
  <svg
    viewBox="0 0 24 24"
    fill="currentColor"
    stroke="currentColor"
    strokeWidth="2"
    strokeLinecap="round"
    strokeLinejoin="round"
    className={className}
  >
    <path d="M12 2C6.477 2 2 6.484 2 12.017c0 4.425 2.865 8.18 6.839 9.504.5.092.682-.217.682-.483 0-.237-.008-.868-.013-1.703-2.782.605-3.369-1.343-3.369-1.343-.454-1.158-1.11-1.466-1.11-1.466-.908-.62.069-.608.069-.608 1.003.07 1.531 1.032 1.531 1.032.892 1.53 2.341 1.088 2.91.832.092-.647.35-1.088.636-1.338-2.22-.253-4.555-1.113-4.555-4.951 0-1.093.39-1.988 1.029-2.688-.103-.253-.446-1.272.098-2.65 0 0 .84-.27 2.75 1.026A9.564 9.564 0 0112 6.844c.85.004 1.705.115 2.504.337 1.909-1.296 2.747-1.027 2.747-1.027.546 1.379.202 2.398.1 2.651.64.7 1.028 1.595 1.028 2.688 0 3.848-2.339 4.695-4.566 4.943.359.309.678.92.678 1.855 0 1.338-.012 2.419-.012 2.747 0 .268.18.58.688.482A10.019 10.019 0 0022 12.017C22 6.484 17.522 2 12 2z" />
  </svg>
);

function App() {
  const { t, i18n } = useTranslation();
  const showDocs = false;

  const toggleLanguage = () => {
    i18n.changeLanguage(i18n.language.startsWith('zh') ? 'en' : 'zh');
  };

  const features = [
    {
      id: 'search',
      icon: <Search className="w-6 h-6 text-blue-500" />,
    },
    {
      id: 'pinyin',
      icon: <Type className="w-6 h-6 text-indigo-500" />,
    },
    {
      id: 'grouping',
      icon: <Layers className="w-6 h-6 text-purple-500" />,
    },
    {
      id: 'quick_close',
      icon: <XCircle className="w-6 h-6 text-pink-500" />,
    },
    {
      id: 'themes',
      icon: <Palette className="w-6 h-6 text-rose-500" />,
    },
    {
      id: 'monitors',
      icon: <Monitor className="w-6 h-6 text-orange-500" />,
    },
  ];

  return (
    <div className="min-h-screen bg-slate-50 dark:bg-slate-900 text-slate-900 dark:text-slate-50 transition-colors duration-300">
      {/* Navbar */}
      <nav className="fixed top-0 w-full bg-white/80 dark:bg-slate-900/80 backdrop-blur-md z-50 border-b border-slate-200 dark:border-slate-800">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <div className="flex justify-between items-center h-16">
            <div className="flex items-center gap-2">
              <img
                src="/flipswitcher.ico"
                alt="FlipSwitcher Icon"
                className="w-8 h-8 rounded-lg shadow-lg object-cover"
              />
              <span className="font-bold text-xl tracking-tight">FlipSwitcher</span>
            </div>

            <div className="hidden md:flex space-x-8">
              <a href="#home" className="text-slate-600 dark:text-slate-300 hover:text-blue-600 dark:hover:text-blue-400 font-medium transition-colors">
                {t('nav.home')}
              </a>
              <a href="#features" className="text-slate-600 dark:text-slate-300 hover:text-blue-600 dark:hover:text-blue-400 font-medium transition-colors">
                {t('nav.features')}
              </a>
              {showDocs && (
                <a href="#docs" className="text-slate-400 dark:text-slate-500 cursor-not-allowed font-medium" title="Coming soon">
                  {t('nav.docs')}
                </a>
              )}
            </div>

            <div className="flex items-center gap-4">
              <button
                onClick={toggleLanguage}
                className="p-2 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors flex items-center gap-2"
                title="Toggle Language"
              >
                <Globe className="w-5 h-5" />
                <span className="text-sm font-medium uppercase">{i18n.language.startsWith('zh') ? 'EN' : '中'}</span>
              </button>
              <a
                href="https://github.com/dianbanjiu/FlipSwitcher"
                target="_blank"
                rel="noopener noreferrer"
                className="p-2 text-slate-600 dark:text-slate-300 hover:bg-slate-100 dark:hover:bg-slate-800 rounded-full transition-colors"
              >
                <GithubIcon className="w-5 h-5" />
              </a>
            </div>
          </div>
        </div>
      </nav>

      {/* Hero Section */}
      <section id="home" className="pt-32 pb-20 px-4 sm:px-6 lg:px-8 max-w-7xl mx-auto flex flex-col items-center text-center">
        <h1 className="text-5xl md:text-7xl font-extrabold tracking-tight mb-6 bg-clip-text text-transparent bg-gradient-to-r from-blue-600 to-indigo-600 dark:from-blue-400 dark:to-indigo-400">
          {t('hero.title')}
        </h1>
        <p className="text-xl md:text-2xl text-slate-600 dark:text-slate-400 max-w-3xl mb-10 leading-relaxed">
          {t('hero.subtitle')}
        </p>

        <div className="flex flex-col sm:flex-row gap-4 mb-16">
          <a
            href="https://github.com/dianbanjiu/FlipSwitcher/releases/latest"
            className="flex items-center justify-center gap-2 bg-blue-600 hover:bg-blue-700 text-white px-8 py-4 rounded-full font-semibold text-lg transition-all shadow-lg hover:shadow-xl hover:-translate-y-0.5"
          >
            <Download className="w-5 h-5" />
            {t('hero.download')}
          </a>
          <a
            href="https://github.com/dianbanjiu/FlipSwitcher"
            className="flex items-center justify-center gap-2 bg-white dark:bg-slate-800 hover:bg-slate-50 dark:hover:bg-slate-700 text-slate-900 dark:text-white px-8 py-4 rounded-full font-semibold text-lg transition-all shadow border border-slate-200 dark:border-slate-700"
          >
            <GithubIcon className="w-5 h-5" />
            {t('hero.view_source')}
          </a>
        </div>
        <p className="text-sm text-slate-500 dark:text-slate-400 mt-[-2rem] mb-12">
          {t('hero.download_sub')}
        </p>

        {/* Screenshot Placeholder */}
        <div className="relative w-full max-w-5xl rounded-2xl shadow-2xl overflow-hidden border border-slate-200 dark:border-slate-700 bg-slate-100 dark:bg-slate-800 aspect-[16/9] sm:aspect-[2/1] flex items-center justify-center">
          {/* We use the raw github image from main branch since it's not in the pages branch */}
          <img
            src="https://raw.githubusercontent.com/dianbanjiu/FlipSwitcher/main/docs/screenshot.png"
            alt="FlipSwitcher Screenshot"
            className="object-cover w-full h-full"
            onError={(e) => {
              (e.target as HTMLImageElement).style.display = 'none';
              (e.target as HTMLImageElement).nextElementSibling?.classList.remove('hidden');
            }}
          />
          <div className="hidden absolute flex-col items-center gap-4 text-slate-400">
            <Layers className="w-16 h-16 opacity-50" />
            <span className="font-medium text-lg">Screenshot Preview</span>
          </div>
        </div>
      </section>

      {/* Features Section */}
      <section id="features" className="py-24 bg-white dark:bg-slate-900 border-t border-slate-200 dark:border-slate-800">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <h2 className="text-3xl md:text-4xl font-bold text-center mb-16">
            {t('features.title')}
          </h2>

          <div className="grid grid-cols-1 md:grid-cols-2 lg:grid-cols-3 gap-8">
            {features.map((feature) => (
              <div
                key={feature.id}
                className="p-8 rounded-2xl bg-slate-50 dark:bg-slate-800/50 border border-slate-100 dark:border-slate-800 hover:shadow-lg transition-all"
              >
                <div className="w-12 h-12 rounded-xl bg-white dark:bg-slate-800 shadow-sm flex items-center justify-center mb-6 border border-slate-100 dark:border-slate-700">
                  {feature.icon}
                </div>
                <h3 className="text-xl font-bold mb-3">{t(`features.items.${feature.id}.title`)}</h3>
                <p className="text-slate-600 dark:text-slate-400 leading-relaxed">
                  {t(`features.items.${feature.id}.desc`)}
                </p>
              </div>
            ))}
          </div>
        </div>
      </section>

      {/* Extensible Docs Placeholder Section */}
      {showDocs && (
        <section id="docs" className="py-24 bg-slate-50 dark:bg-slate-900/50 border-t border-slate-200 dark:border-slate-800">
          <div className="max-w-4xl mx-auto px-4 sm:px-6 lg:px-8 text-center">
            <BookOpen className="w-12 h-12 mx-auto text-slate-400 mb-6" />
            <h2 className="text-2xl md:text-3xl font-bold mb-4">
              {t('nav.docs')}
            </h2>
            <p className="text-slate-600 dark:text-slate-400">
              We are working on detailed documentation to help you get the most out of FlipSwitcher. Check back soon!
            </p>
          </div>
        </section>
      )}

      {/* Footer */}
      <footer className="py-12 bg-white dark:bg-slate-950 border-t border-slate-200 dark:border-slate-900 text-center">
        <div className="max-w-7xl mx-auto px-4 sm:px-6 lg:px-8">
          <p className="text-slate-600 dark:text-slate-400 font-medium mb-2">
            {t('footer.made_with')}
          </p>
          <p className="text-sm text-slate-500 dark:text-slate-500">
            {t('footer.license')}
          </p>
        </div>
      </footer>
    </div>
  );
}

export default App;

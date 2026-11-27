# Mason Agent for Linux: a C++ engine (masoncore), the mason command line tool and the GTK 4 / libadwaita app.
#   meson setup builddir && meson compile -C builddir && meson test -C builddir
# Use -Dapp=false to build only the engine and command line (no GTK needed).
project('mason-agent', 'cpp',
  version: '2.4.0',
  default_options: ['cpp_std=c++20', 'warning_level=2', 'buildtype=debugoptimized'],
  meson_version: '>= 1.1')

fs = import('fs')
curl = dependency('libcurl')
json = dependency('nlohmann_json')
threads = dependency('threads')
# AT-SPI lets Mason read and operate a window the user attaches. Without it the build has no window tools.
atspi = dependency('atspi-2', required: false)
gobject = dependency('gobject-2.0', required: atspi.found())
core_args = atspi.found() ? ['-DMASON_HAVE_ATSPI'] : []
# Xlib takes the picture of an attached window for desktop_visual, on an X11 session.
x11 = dependency('x11', required: false)
if x11.found()
  core_args += '-DMASON_HAVE_X11'
endif

core_sources = files(
  'core/util.cpp', 'core/workspace.cpp', 'core/session.cpp', 'core/model.cpp',
  'core/shell.cpp', 'core/harness.cpp', 'core/tools.cpp', 'core/browser.cpp', 'core/planning.cpp', 'core/semantic.cpp', 'core/desktop.cpp', 'core/acceptance.cpp', 'core/transfer.cpp', 'core/navigation.cpp', 'core/language.cpp', 'core/generation.cpp', 'core/gpu.cpp', 'core/notes.cpp', 'core/palette.cpp', 'core/script.cpp', 'core/experiments.cpp', 'core/delivery.cpp', 'core/background.cpp', 'core/roles.cpp', 'core/project_tests.cpp', 'core/preview.cpp', 'core/network.cpp', 'core/policy.cpp', 'core/bundles.cpp', 'core/browse.cpp', 'core/derived.cpp', 'core/mcp.cpp', 'core/chat.cpp', 'core/schedules.cpp', 'core/agent.cpp', 'core/models.cpp', 'core/model_memory.cpp', 'core/theme.cpp',
)
core = static_library('masoncore', core_sources, dependencies: [curl, json, threads, atspi, gobject, x11], cpp_args: core_args)
core_dep = declare_dependency(link_with: core, include_directories: include_directories('core'), dependencies: [curl, json, threads, atspi, gobject, x11])

version_define = '-DMASON_VERSION="' + meson.project_version() + '"'
executable('mason', files('cli/main.cpp'), dependencies: core_dep, cpp_args: version_define, install: true)

# The Python program that serves Transformers weights, and the list of architectures it accepts.
install_data('../windows/scripts/Serve-Transformers.py', '../windows/scripts/ModelAdapters.json', install_dir: get_option('datadir') / 'mason-agent' / 'transformers')

# Jedi and the TypeScript compiler for inspect_symbol. packaging/prepare-language-tools.sh fetches the pinned
# versions into linux/language-tools; without that folder the tool is simply not offered.
install_data('../windows/scripts/language-tools/python-symbols.py', '../windows/scripts/language-tools/typescript-symbols.cjs', install_dir: get_option('datadir') / 'mason-agent' / 'language-tools')
if fs.is_dir('language-tools/python')
  install_subdir('language-tools/python', install_dir: get_option('datadir') / 'mason-agent' / 'language-tools')
endif
if fs.is_dir('language-tools/typescript')
  install_subdir('language-tools/typescript', install_dir: get_option('datadir') / 'mason-agent' / 'language-tools')
endif

tests = executable('mason-tests', files('tests/tests.cpp'), dependencies: core_dep)
test('engine', tests, timeout: 120)

if get_option('app')
  gtkmm = dependency('gtkmm-4.0', version: '>= 4.10')
  adwaita = dependency('libadwaita-1', version: '>= 1.4')
  # The Website panel shows pages in a WebKitGTK view. Without it the app still builds, and pages open in the user's browser.
  webkit = dependency('webkitgtk-6.0', required: false)
  app_args = webkit.found() ? ['-DMASON_HAVE_WEBKIT'] : []
  gnome = import('gnome')
  app_resources = gnome.compile_resources('mason-resources', 'app/mason.gresource.xml', source_dir: ['packaging', '../windows/src/MasonAgent/Assets'])
  app_sources = files('app/main.cpp', 'app/window.cpp', 'app/style.cpp', 'app/models_page.cpp', 'app/model_detail.cpp', 'app/chat_page.cpp', 'app/settings_page.cpp', 'app/controller.cpp', 'app/panels.cpp', 'app/notes_tray.cpp')
  executable('mason-agent', app_sources, app_resources, dependencies: [core_dep, gtkmm, adwaita, webkit], cpp_args: app_args, install: true)
  install_data('packaging/site.masonagent.MasonAgent.desktop', install_dir: get_option('datadir') / 'applications')
  install_data('packaging/site.masonagent.MasonAgent.metainfo.xml', install_dir: get_option('datadir') / 'metainfo')
  install_data('packaging/site.masonagent.MasonAgent.png', install_dir: get_option('datadir') / 'icons/hicolor/512x512/apps')
endif

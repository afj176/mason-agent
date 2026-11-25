# Mason Agent for Linux: a C++ engine (masoncore), the mason command line tool and the GTK 4 / libadwaita app.
#   meson setup builddir && meson compile -C builddir && meson test -C builddir
# Use -Dapp=false to build only the engine and command line (no GTK needed).
project('mason-agent', 'cpp',
  version: '2.2.1',
  default_options: ['cpp_std=c++20', 'warning_level=2', 'buildtype=debugoptimized'],
  meson_version: '>= 1.1')

curl = dependency('libcurl')
json = dependency('nlohmann_json')
threads = dependency('threads')

core_sources = files(
  'core/util.cpp', 'core/workspace.cpp', 'core/session.cpp', 'core/model.cpp',
  'core/shell.cpp', 'core/agent.cpp', 'core/models.cpp',
)
core = static_library('masoncore', core_sources, dependencies: [curl, json, threads])
core_dep = declare_dependency(link_with: core, include_directories: include_directories('core'), dependencies: [curl, json, threads])

executable('mason', files('cli/main.cpp'), dependencies: core_dep, install: true)

tests = executable('mason-tests', files('tests/tests.cpp'), dependencies: core_dep)
test('engine', tests, timeout: 120)

if get_option('app')
  gtkmm = dependency('gtkmm-4.0', version: '>= 4.10')
  adwaita = dependency('libadwaita-1', version: '>= 1.4')
  app_sources = files('app/main.cpp', 'app/window.cpp', 'app/models_page.cpp', 'app/controller.cpp')
  executable('mason-agent', app_sources, dependencies: [core_dep, gtkmm, adwaita], install: true)
  install_data('packaging/site.masonagent.MasonAgent.desktop', install_dir: get_option('datadir') / 'applications')
  install_data('packaging/site.masonagent.MasonAgent.metainfo.xml', install_dir: get_option('datadir') / 'metainfo')
  install_data('../brand/logo-kit/favicon/android-chrome-512x512.png',
    rename: 'site.masonagent.MasonAgent.png', install_dir: get_option('datadir') / 'icons/hicolor/512x512/apps')
endif
